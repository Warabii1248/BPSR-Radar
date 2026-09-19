using System.Buffers.Binary;

// Guards the rule that keeps the helper cheap: once the record's address is
// known, it is held for the life of the game process and memory is never
// walked again.
//
// Measured 2026-09-19 from the session that ran 14:55 to 15:01, counting
// only this scanner's log lines (`scan ms=N cand=...`):
//
//   scans                  90
//   of those, cand=0       57      no usable entity list to match against
//   scan cost          median 2092 ms, p90 2420 ms, max 2817 ms
//   time spent scanning   188 s of about seven minutes, a 45% duty cycle
//   record address     0x230d47bb950 in every single lock, start to finish
//
// An earlier version of this comment said 122 scans and a 6222 ms maximum.
// That count came from grepping for "scan ms=", which also matches the OLD
// element scanner's "rescan ms=" lines, and 6222 ms was one of those, logged
// at 11:32:06 by a scanner this code no longer uses. The conclusion does not
// change and 45% is still most of a duty cycle, but the numbers were wrong.
//
// The address never moved -- not across clears, target switches or a zone
// change -- so all 90 scans but the first were waste. They happened because
// the loop rescanned on a timer: every 15 s while a manual lock was held and
// every 3 s otherwise, which is most of the time.
//
// On the labels. At 9de20b4 there was no ShouldScan: the loop asked
// `records.Count == 0 || lastScan.Elapsed >= (lastUuid != 0 ? 15000 : 3000)`.
// Translated into this predicate's arguments that is true for EVERY
// haveRecord:false case, so only a check of the form
// `!ShouldScan(haveRecord: false, ...)` can fail against it. Those are the
// real controls. A check that asserts true with haveRecord:false cannot fail
// against that code; where one guards a clause that could be deleted today it
// says MUTATION, which is the weaker claim it actually is.
//
// The retention rule deliberately does not use the signature. A cleared slot
// happens to still satisfy it (Parse accepts zeroed positions and kind 0, so
// it reports Valid with Uuid 0), but that is incidental and unmeasured --
// what the game leaves in the record on release was never observed directly.
// Keying on the owner pointer is correct whatever the clear looks like, and
// HoldsShapesTheSignatureRejects covers the case where it does not hold.
static class SelfTestHold
{
    private const ulong SlotUuidAddr = 0x0002_0000 + LockRecord.Before;
    private const ulong Owner = 0x0000_0230_d47b_b000;
    private const ulong MonsterA = 0x1a78b0040;
    private const ulong MonsterB = 0x1a7930040;

    public static int Run()
    {
        int failures = 0;
        failures += TimerRescan();
        failures += Retention();
        failures += Transition();
        failures += StopRequest();
        failures += GateOrder();
        failures += StopRequestArgument();
        Console.WriteLine(failures == 0 ? "selftest-hold: PASS" : $"selftest-hold: FAIL ({failures})");
        return failures == 0 ? 0 : 1;
    }

    // The fix that removed 89 of the 90 scans.
    private static int TimerRescan()
    {
        int f = 0;

        // NEGATIVE CONTROL. The old policy says yes here whether or not a
        // lock was held -- 15 s with, 3 s without -- and that is what spent
        // 45% of a session scanning. An hour covers both thresholds.
        f += Check("NEGATIVE CONTROL: a known record is never rescanned, not after an hour",
            !Program.ShouldScan(haveRecord: true, msSinceScan: 3_600_000, knownMonsters: 400, knownFresh: true));
        // Weaker: the 3 s threshold only applied with no lock held, and this
        // predicate has no argument for that, so this is a mutation check.
        f += Check("MUTATION: nor after three seconds",
            !Program.ShouldScan(haveRecord: true, msSinceScan: 3_000, knownMonsters: 400, knownFresh: true));

        // The entity-list gate. 57 of the 90 scans ran without a usable
        // list and found nothing; the scan cannot succeed without one, so it
        // is free to skip. This is also the startup delay before the first
        // lock.
        //
        // The gate counts monsters, not entities. The eight uuids below are
        // the exact entity list that was live at 15:19 on 2026-09-19, when
        // the first version of this gate was running: known=8/fresh and
        // cand=0 on a two second scan every five seconds, standing away from
        // anything attackable. Players and NPCs pass "the list is not empty"
        // and match nothing, because the scan only ever accepts a monster.
        var bystanders = new KnownEntities("");
        bystanders.Seed(new ulong[]
        {
            0x7b4a80080, 0xbf738200, 0xa8dbf60280, 0x22994f0280,
            0xce49800280, 0xbf988200, 0x1930ba0280, 0xbf9c8200,
        });
        f += Check("that measured list has eight entities and no monsters",
            bystanders.Count == 8 && bystanders.MonsterCount == 0);
        f += Check("so it does not justify a scan",
            !Program.ShouldScan(haveRecord: false, msSinceScan: long.MaxValue,
                knownMonsters: bystanders.MonsterCount, knownFresh: true));
        // MUTATION, not a control: it asserts true, and the old policy said
        // true for every haveRecord:false case. What it guards is the call
        // site passing Known.Count instead of Known.MonsterCount -- a live
        // hazard, since that swap was made once and shipped -- but only
        // GateOrder below actually runs the call site.
        f += Check("MUTATION: gating on the entity count would scan anyway",
            Program.ShouldScan(haveRecord: false, msSinceScan: long.MaxValue,
                knownMonsters: bystanders.Count, knownFresh: true));

        var withMonster = new KnownEntities("");
        withMonster.Seed(new ulong[] { 0xa8dbf60280, MonsterA });
        f += Check("one monster in the list is enough",
            withMonster.MonsterCount == 1
            && Program.ShouldScan(haveRecord: false, msSinceScan: long.MaxValue,
                knownMonsters: withMonster.MonsterCount, knownFresh: true));

        f += Check("no scan without an entity list",
            !Program.ShouldScan(haveRecord: false, msSinceScan: long.MaxValue, knownMonsters: 0, knownFresh: true));
        f += Check("no scan with a stale entity list",
            !Program.ShouldScan(haveRecord: false, msSinceScan: long.MaxValue, knownMonsters: 400, knownFresh: false));

        f += Check("the first scan does not wait out the backoff",
            Program.ShouldScan(haveRecord: false, msSinceScan: long.MaxValue, knownMonsters: 400, knownFresh: true));
        f += Check("a failed scan backs off before retrying",
            !Program.ShouldScan(haveRecord: false, msSinceScan: 100, knownMonsters: 400, knownFresh: true));
        f += Check("and retries once the backoff is up",
            Program.ShouldScan(haveRecord: false, msSinceScan: 5_000, knownMonsters: 400, knownFresh: true));

        // Acquiring the record after a fresh start. Measured 2026-09-19,
        // entering a dungeon:
        //
        //   15:50:51  scan 1802 ms  cand=201  rec=0  known=18/mon=4
        //   15:50:57  scan 1949 ms  cand=280  rec=0  known=30/mon=11
        //   15:51:01  scan 1738 ms  cand=338  rec=1
        //
        // Ten seconds. The scan only accepts a uuid the packet side names, so
        // the first two could not have matched the locked monster: the entity
        // list was still filling. Waiting out a fixed backoff after a scan
        // that failed for that reason wastes the very seconds in which the
        // list becomes usable.
        // MUTATION: delete the `knownMonsters > monstersAtLastScan` clause
        // and this fails. Not a control against 9de20b4, which had no backoff
        // for it to beat.
        f += Check("MUTATION: the list going from 4 monsters to 11 retries at once",
            Program.ShouldScan(haveRecord: false, msSinceScan: 600, knownMonsters: 11, knownFresh: true,
                monstersAtLastScan: 4));
        f += Check("a list that has not grown does not",
            !Program.ShouldScan(haveRecord: false, msSinceScan: 600, knownMonsters: 4, knownFresh: true,
                monstersAtLastScan: 4));

        // The lock key. Pressing it means a lock is being held right now,
        // which is the only state the scan can succeed in.
        f += Check("the lock key starts a scan immediately",
            Program.ShouldScan(haveRecord: false, msSinceScan: 500, knownMonsters: 4, knownFresh: true,
                monstersAtLastScan: 4, lockKeyPressed: true));
        f += Check("but not while a record is already known",
            !Program.ShouldScan(haveRecord: true, msSinceScan: 500, knownMonsters: 4, knownFresh: true,
                monstersAtLastScan: 4, lockKeyPressed: true));
        f += Check("and not without monsters to match",
            !Program.ShouldScan(haveRecord: false, msSinceScan: 500, knownMonsters: 0, knownFresh: true,
                monstersAtLastScan: 0, lockKeyPressed: true));

        // Neither urgent trigger may turn into a scan loop.
        f += Check("a held key cannot scan faster than the floor",
            !Program.ShouldScan(haveRecord: false, msSinceScan: 50, knownMonsters: 4, knownFresh: true,
                monstersAtLastScan: 4, lockKeyPressed: true));
        f += Check("nor can a list growing one entity at a time",
            !Program.ShouldScan(haveRecord: false, msSinceScan: 50, knownMonsters: 12, knownFresh: true,
                monstersAtLastScan: 11));
        return f;
    }

    // Crossing a field boundary.
    //
    // Measured 2026-09-19, either side of a transition:
    //
    //   15:33:38  rec=0x2a64e3ec950                       before
    //   15:34:44  scan 1944 ms  cand=271  rec=0           nothing locked
    //   15:34:49  scan 1949 ms  cand=408  rec=0           nothing locked
    //   15:34:54  scan 1759 ms            rec=2           found
    //   15:34:56  rec=0x2a64e3ec950                       the same address
    //
    // The address did not move; the scene was rebuilt under it, which changes
    // the owner pointer, and the poll drops an address whose owner changed.
    // Two of the three scans then failed, because the scan matches on the
    // uuid in the slot and so can only find the record while a lock is held.
    // About five seconds before the first target appeared.
    //
    // Re-adoption tries those few addresses first. On the measured data it
    // takes one poll -- 100 ms -- and no scan at all.
    private static int Transition()
    {
        int f = 0;
        var mem = new FakeMem(0x0001_0000, 0x0010_0000);
        var scratch = new byte[LockRecord.ReadSize];
        var known = new KnownEntities("");
        known.Seed(new[] { MonsterA, MonsterB });
        var sticky = new[] { SlotUuidAddr };
        var owners = new Dictionary<ulong, ulong>();

        Set(mem, MonsterA, manual: true);
        f += Check("the address is remembered while it works",
            Program.Readopt(mem, sticky, known, scratch, owners).Contains(SlotUuidAddr));

        // The transition: same address, new object, new owner, new target.
        ulong after = Owner + 0x8000;
        Set(mem, MonsterB, manual: true);
        BinaryPrimitives.WriteUInt64LittleEndian(mem.Span(SlotUuidAddr - LockRecord.Before), after);

        // Not a control: both sides come from this test. It states the
        // premise the rest of the case rests on -- the owner really does
        // differ after a transition, so the poll drops the address.
        LockRecord.TryReadSlot(mem, SlotUuidAddr, scratch, out ulong nowOwner, out _);
        f += Check("premise: the owner differs after a transition, so the poll drops it",
            owners[SlotUuidAddr] != nowOwner);

        var back = Program.Readopt(mem, sticky, known, scratch, owners);
        f += Check("re-adoption takes it back without a scan", back.Contains(SlotUuidAddr));
        f += Check("and records the new owner", owners[SlotUuidAddr] == after);

        // It must not take back anything it cannot confirm.
        Clear(mem, after);
        f += Check("a cleared slot is not re-adopted",
            Program.Readopt(mem, sticky, known, scratch, owners).Count == 0);

        var stranger = new KnownEntities("");
        stranger.Seed(new[] { MonsterB });
        Set(mem, MonsterA, manual: true);
        f += Check("a uuid the packet side does not name is not re-adopted",
            Program.Readopt(mem, sticky, stranger, scratch, owners).Count == 0);

        BinaryPrimitives.WriteUInt64LittleEndian(mem.Span(SlotUuidAddr), 0xa8dbf60280);
        f += Check("a player in the slot is not re-adopted",
            Program.Readopt(mem, sticky, known, scratch, owners).Count == 0);

        f += Check("an address that reads as nothing is not re-adopted",
            Program.Readopt(mem, new ulong[] { 0x9000_0000_0000 }, known, scratch, owners).Count == 0);
        return f;
    }

    // The order the watch loop asks its questions in.
    //
    // Everything else here tests a predicate in isolation, which leaves the
    // call sites uncovered: the arguments they pass and the order they run in
    // are where two of the defects being fixed actually lived. This covers
    // the gates; StopRequestArgument covers the other one.
    private static int GateOrder()
    {
        int f = 0;

        // A list nothing has loaded is stale by construction.
        var nothing = new KnownEntities("");
        f += Check("no usable entity list means wait, not scan",
            Program.CheckGate(haveRecord: false, nothing) == Program.Gate.WaitForEntities);

        // NEGATIVE CONTROL. These are the eight entities measured at 15:19 on
        // 2026-09-19 -- players and NPCs, not one monster among them. The
        // version that counted entities rather than monsters scanned for two
        // seconds every five on exactly this input. CheckGate takes the list
        // itself now, so the wrong count can no longer be passed by mistake;
        // this fails if the clause inside it goes back to Count.
        var bystanders = new KnownEntities("");
        bystanders.Seed(new ulong[]
        {
            0x7b4a80080, 0xbf738200, 0xa8dbf60280, 0x22994f0280,
            0xce49800280, 0xbf988200, 0x1930ba0280, 0xbf9c8200,
        });
        f += Check("NEGATIVE CONTROL: eight players and NPCs do not open the gate",
            bystanders.Count == 8 && bystanders.MonsterCount == 0
            && Program.CheckGate(haveRecord: false, bystanders) == Program.Gate.NoMonsters);

        var withMonster = new KnownEntities("");
        withMonster.Seed(new ulong[] { 0xa8dbf60280, MonsterA });
        f += Check("one monster in the list opens it",
            Program.CheckGate(haveRecord: false, withMonster) == Program.Gate.Proceed);

        // A record being polled must outrank both gates, or a moment of stale
        // entity data would stop the loop reading a record it already has.
        f += Check("a record already held is polled whatever the entity list says",
            Program.CheckGate(haveRecord: true, nothing) == Program.Gate.Proceed);
        return f;
    }

    // The argument StopRequested passes, which is where the defect was: the
    // predicate was fine, the call site handed it the game's pid. This runs
    // the real StopRequested against a real file, so it fails if that
    // argument regresses -- which no amount of testing StopRequestMatches can.
    private static int StopRequestArgument()
    {
        int f = 0;
        string dir = Path.Combine(Path.GetTempPath(), "BPSR-Radar", "selftest-stop");
        string saved = Program.StatePathForTests;
        try
        {
            Directory.CreateDirectory(dir);
            // Point the helper's whole work directory at a scratch copy, so a
            // live app's pending request is never read or rewritten.
            Program.StatePathForTests = Path.Combine(dir, "locktarget.json");
            string stop = Path.Combine(dir, "helper-stop");

            File.WriteAllText(stop, Environment.ProcessId.ToString());
            f += Check("NEGATIVE CONTROL: a request naming this process retires it",
                Program.StopRequestedForTests());
            f += Check("and the request is consumed", !File.Exists(stop));

            File.WriteAllText(stop, "4242");
            f += Check("a request naming someone else is ignored", !Program.StopRequestedForTests());
            f += Check("and is left alone for whoever it names", File.Exists(stop));

            File.WriteAllText(stop, $"4242,{Environment.ProcessId},99");
            f += Check("one name out of several still retires this process",
                Program.StopRequestedForTests());
            string rest = File.ReadAllText(stop);
            f += Check("and the others keep theirs",
                rest.Contains("4242") && rest.Contains("99")
                && !rest.Contains(Environment.ProcessId.ToString()));

            try { File.Delete(stop); } catch { }
            f += Check("no request file means no retirement", !Program.StopRequestedForTests());
        }
        catch (Exception ex)
        {
            f += Check($"stop-request check ran ({ex.GetType().Name})", false);
        }
        finally
        {
            Program.StatePathForTests = saved;
            try { Directory.Delete(dir, true); } catch { }
        }
        return f;
    }

    // The helper's self-retirement, which had never worked.
    //
    // The app writes the helper's own process id into the request file
    // (LockTargetService.RequestHelperStop passes p.Id). The helper compared
    // it against the *game's* pid, so no request ever matched. Measured
    // 2026-09-19: writing the helper's pid did nothing, writing the game's
    // pid retired it immediately. An earlier session blamed this on a helper
    // built before the mechanism existed.
    private static int StopRequest()
    {
        int f = 0;
        const int Self = 52160;
        const int Game = 46184;

        // Neither of these is a control. The defect was the ARGUMENT at the
        // call site -- StopRequested passed curPid, the game's pid -- and
        // StopRequestMatches is new, so nothing here can fail against the old
        // code. GateOrder covers the argument.
        f += Check("the helper's own pid is what retires it",
            Program.StopRequestMatches(Self.ToString(), Self));
        f += Check("compared against the game's pid, a real request never matches",
            !Program.StopRequestMatches(Self.ToString(), Game));
        f += Check("a list naming several helpers retires each of them",
            Program.StopRequestMatches($"{Game},{Self},12345", Self)
            && Program.StopRequestMatches($"{Game},{Self},12345", 12345)
            && !Program.StopRequestMatches($"{Game},{Self},12345", 999));
        f += Check("the game's pid in the file does not retire a helper",
            !Program.StopRequestMatches(Game.ToString(), Self));

        // A request left behind by a helper that already retired must not
        // take the next one with it.
        f += Check("a request naming another helper is ignored",
            !Program.StopRequestMatches("12345", Self));
        f += Check("surrounding whitespace is tolerated",
            Program.StopRequestMatches(" " + Self + Environment.NewLine, Self));
        f += Check("an empty request file is ignored", !Program.StopRequestMatches("", Self));
        f += Check("a missing request file is ignored", !Program.StopRequestMatches(null, Self));
        f += Check("garbage is ignored", !Program.StopRequestMatches("not a pid", Self));
        return f;
    }

    // The address survives whatever the game leaves in the record.
    private static int Retention()
    {
        int f = 0;
        var mem = new FakeMem(0x0001_0000, 0x0010_0000);
        var scratch = new byte[LockRecord.ReadSize];

        Set(mem, MonsterA, manual: true);
        f += Check("locked slot parses", LockRecord.TryRead(mem, SlotUuidAddr, scratch, out var v) && v.Uuid == MonsterA);
        f += Check("locked slot reports manual", v.LockKind == LockRecord.Kind.Manual);
        f += Check("slot read exposes the owner",
            LockRecord.TryReadSlot(mem, SlotUuidAddr, scratch, out ulong owner1, out _) && owner1 == Owner);

        // Three shapes a release could leave behind. Which one the game
        // actually writes was not measured, so all three must hold.
        f += HeldAfter(mem, scratch, "a fully zeroed slot", w => { w[..LockRecord.ReadSize].Clear(); Own(w); });
        f += HeldAfter(mem, scratch, "the uuid alone cleared", w => BinaryPrimitives.WriteUInt64LittleEndian(w[0x08..], 0));
        // HoldsShapesTheSignatureRejects: garbage where the position was, so
        // Parse fails. TryRead -- the old way of deciding an address is still
        // good -- returns false here; the owner still says the slot is ours.
        f += HeldAfter(mem, scratch, "a shape the signature rejects", w =>
        {
            BinaryPrimitives.WriteUInt64LittleEndian(w[0x08..], 0);
            BinaryPrimitives.WriteUInt64LittleEndian(w[0x20..], 0x7ff8_0000_dead_beef);
            BinaryPrimitives.WriteUInt64LittleEndian(w[0x38..], 0x4242);
        });
        f += Check("the signature really does reject that last shape",
            !LockRecord.TryRead(mem, SlotUuidAddr, scratch, out _));

        // Locked again, different monster, same address.
        Set(mem, MonsterB, manual: false);
        f += Check("the same address holds the next lock",
            LockRecord.TryRead(mem, SlotUuidAddr, scratch, out var v3) && v3.Uuid == MonsterB);
        f += Check("the next lock's kind is read", v3.LockKind == LockRecord.Kind.Auto);

        // A record that genuinely moved: the owner is how that is noticed.
        BinaryPrimitives.WriteUInt64LittleEndian(mem.Span(SlotUuidAddr - LockRecord.Before), Owner + 0x1000);
        f += Check("a changed owner is visible to the caller",
            LockRecord.TryReadSlot(mem, SlotUuidAddr, scratch, out ulong owner4, out _) && owner4 != Owner);

        // Justifies the entity-list gate above: without a list the scan
        // cannot find the record even when it is sitting right there.
        Set(mem, MonsterA, manual: true);
        var known = new KnownEntities("");
        known.Seed(new[] { MonsterA, MonsterB });
        var empty = new KnownEntities("");
        empty.Seed(Array.Empty<ulong>());
        f += Check("the scan finds the record when the entity list has it",
            LockRecord.Find(mem, known, out _).Contains(SlotUuidAddr));
        f += Check("the scan finds nothing without an entity list, so skipping it is free",
            LockRecord.Find(mem, empty, out _).Count == 0);
        return f;
    }

    private static int HeldAfter(FakeMem mem, byte[] scratch, string label, Action<Span<byte>> mutate)
    {
        Set(mem, MonsterA, manual: true);
        mutate(mem.Span(SlotUuidAddr - LockRecord.Before));
        bool held = LockRecord.TryReadSlot(mem, SlotUuidAddr, scratch, out ulong owner, out _) && owner == Owner;
        return Check($"the address is held through {label}", held);
    }

    private static int Check(string label, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {label}");
        return ok ? 0 : 1;
    }

    private static void Own(Span<byte> w) => BinaryPrimitives.WriteUInt64LittleEndian(w, Owner);

    // Released: the slot stays, the target goes.
    private static void Clear(FakeMem mem, ulong owner)
    {
        var w = mem.Span(SlotUuidAddr - LockRecord.Before);
        w[..LockRecord.ReadSize].Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(w, owner);
    }

    // Writes the record as the game holds it, at SlotUuidAddr - Before.
    private static void Set(FakeMem mem, ulong uuid, bool manual)
    {
        var w = mem.Span(SlotUuidAddr - LockRecord.Before);
        w[..LockRecord.ReadSize].Clear();
        Own(w);
        BinaryPrimitives.WriteUInt64LittleEndian(w[0x08..], uuid);
        BinaryPrimitives.WriteDoubleLittleEndian(w[0x20..], -142.5);
        BinaryPrimitives.WriteDoubleLittleEndian(w[0x28..], 311.75);
        BinaryPrimitives.WriteUInt64LittleEndian(w[0x38..], manual ? 1UL : 0UL);
    }

    private sealed class FakeMem : IMemSource
    {
        private readonly ulong baseAddr;
        private readonly byte[] bytes;

        public FakeMem(ulong baseAddr, int size)
        {
            this.baseAddr = baseAddr;
            bytes = new byte[size];
        }

        public Span<byte> Span(ulong addr) => bytes.AsSpan((int)(addr - baseAddr));

        public List<(ulong Base, ulong Size)> Regions(bool privateOnly) =>
            new() { (baseAddr, (ulong)bytes.Length) };

        public bool Read(ulong addr, byte[] buffer, int length, out int read)
        {
            read = 0;
            if (addr < baseAddr) return false;
            ulong off = addr - baseAddr;
            if (off >= (ulong)bytes.Length) return false;
            read = (int)Math.Min((ulong)length, (ulong)bytes.Length - off);
            Array.Copy(bytes, (int)off, buffer, 0, read);
            return true;
        }

        public string Describe => "fake";
    }
}
