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
    // Real uuids from the name cache, both "Tripod Oculopod" (checked
    // 2026-09-19). Noted because an audit could not tell them from values
    // invented to satisfy the bit shape, which is the failure mode the
    // fixture rule exists for.
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
        failures += PublishThrottle();
        failures += LockKeyEdge();
        failures += ScanDutyCycle();
        failures += DeadRegionStride();
        failures += SceneInvalidation();
        failures += ProvisionalRecordSet();
        failures += DiagnosticsCanFire();
        failures += RecordSetWidens();
        failures += PollIsNotDiscovery();
        failures += PositionFieldIsAFloatPair();
        Console.WriteLine(failures == 0 ? "selftest-hold: PASS" : $"selftest-hold: FAIL ({failures})");
        return failures == 0 ? 0 : 1;
    }

    // The state file is written on a change or on the heartbeat, not on
    // every poll. At the old poll rate the waiting_for_lock branch rewrote it
    // ten times a second for an unchanged reading; at 25ms that would be
    // forty. The risk of getting this wrong is the opposite of the one it
    // fixes -- a suppressed change freezes the overlay -- so the change cases
    // are asserted first and hardest.
    private static int PublishThrottle()
    {
        int f = 0;
        const string watching = "watching";

        // A new target, immediately, however recently the last write went out.
        f += Check("a changed uuid publishes at once",
            Program.ShouldPublish(0x140040, watching, 0x1a0040, watching, msSincePublish: 0));
        // Clearing the lock is a change like any other. Suppressing it would
        // leave the last target on screen after the player let go.
        f += Check("clearing to 0 publishes at once",
            Program.ShouldPublish(0, watching, 0x140040, watching, msSincePublish: 0));
        // Same target, different state: waiting_for_lock -> watching is what
        // the app switches the overlay text on.
        f += Check("a changed state publishes at once",
            Program.ShouldPublish(0, watching, 0, "waiting_for_lock", msSincePublish: 0));

        // First publish of the session. The sentinel is -1 rather than 0
        // precisely so that "nothing is locked" is a reading and not the
        // absence of one.
        f += Check("the first reading publishes even when it is 0",
            Program.ShouldPublish(0, "waiting_for_lock", -1, "", msSincePublish: 0));

        // NEGATIVE CONTROL: the case the throttle exists for. Unchanged, and
        // the heartbeat has not come due, so nothing is written.
        f += Check("NEGATIVE CONTROL: an unchanged reading is not rewritten",
            !Program.ShouldPublish(0x140040, watching, 0x140040, watching, msSincePublish: 25));
        f += Check("MUTATION: still not at one poll short of the heartbeat",
            !Program.ShouldPublish(0x140040, watching, 0x140040, watching, msSincePublish: 1999));

        // But it must stay fresh: the app treats a reading older than 30s as
        // stale and stops believing the helper at all.
        f += Check("an unchanged reading is republished on the heartbeat",
            Program.ShouldPublish(0x140040, watching, 0x140040, watching, msSincePublish: 2000));
        f += Check("and well before the app's 30s staleness cutoff",
            Program.ShouldPublish(0x140040, watching, 0x140040, watching, msSincePublish: 5000));

        return f;
    }

    // The lock key is one of the three ways a scan gets started, and the
    // only one that says "a lock is being held RIGHT NOW" -- which is the
    // one state the scan can succeed in, since it matches on the uuid in the
    // slot. It had never once fired: measured 2026-09-19, 227 scans in a
    // session with LockKeyVk=4 configured and reaching the helper, none of
    // them attributed to the key.
    //
    // The cause was reading bit 15 alone. The loop samples at one point per
    // pass and a scan blocks it for a second or more, so a middle click had
    // to still be physically held at the instant of a sample.
    private static int LockKeyEdge()
    {
        int f = 0;
        bool down;

        // The case that never worked: pressed and released while the loop was
        // busy. Bit 15 is clear by the time anyone looks, bit 0 remembers.
        f += Check("a press that began and ended between samples is caught",
            Program.IsLockKeyEdge(unchecked((short)0x0001), wasDown: false, out down));
        f += Check("and it does not claim the key is still down",
            !down);

        // Held down across the sample, first time seen -- and with bit 0
        // CLEAR, because another process read the async state first and took
        // it. 0x8001 was used here originally, which sets both bits and so
        // never exercised the bit 15 term alone: an implementation of
        // `return pressedSinceLastCall;` passed the whole suite. The shared
        // bit 0 makes this the realistic case, not a contrived one.
        f += Check("a key found down for the first time is an edge, with bit 0 stolen",
            Program.IsLockKeyEdge(unchecked((short)0x8000), wasDown: false, out down) && down);
        f += Check("and both bits together is still one edge",
            Program.IsLockKeyEdge(unchecked((short)0x8001), wasDown: false, out down) && down);

        // NEGATIVE CONTROL: holding the button must not scan over and over.
        // MinScanGapMs is the other guard, but the edge itself has to be one
        // edge per press or a held button becomes a scan loop.
        f += Check("NEGATIVE CONTROL: a key still held is not a new edge",
            !Program.IsLockKeyEdge(unchecked((short)0x8000), wasDown: true, out down) && down);

        // NEGATIVE CONTROL: nothing happened at all.
        f += Check("NEGATIVE CONTROL: an idle key is not an edge",
            !Program.IsLockKeyEdge(0, wasDown: false, out down) && !down);

        // Release, with no press since the last look.
        f += Check("releasing is not an edge",
            !Program.IsLockKeyEdge(0, wasDown: true, out down) && !down);

        // Held, and bit 0 says it was re-pressed since the last call: a
        // second click inside one sampling gap. That is a new edge even
        // though the key looked down both times.
        f += Check("a re-press within one gap is an edge even while held",
            Program.IsLockKeyEdge(unchecked((short)0x8001), wasDown: true, out down) && down);

        return f;
    }

    // The urgent triggers skip the backoff, so the only thing standing
    // between them and a continuous scan loop is the floor. A fixed 400ms
    // floor was enough while the lock key never fired; now that it does, a
    // player pressing lock in a field where the record cannot be found would
    // have put an all-core sweep back to an 83% duty cycle. The floor tracks
    // the measured cost of the last scan instead.
    private static int ScanDutyCycle()
    {
        int f = 0;
        var mon = new KnownEntities("");
        mon.Seed(new ulong[] { 0x140040 });

        // NEGATIVE CONTROL: the lock key pressed again 500ms after a two
        // second scan ended. Past the fixed 400ms gap, so the old floor let
        // it through; the scan costs five times the wait.
        f += Check("NEGATIVE CONTROL: a key press does not restart a 2s scan after 500ms",
            !Program.ShouldScan(haveRecord: false, msSinceScan: 500, knownLockable: 1, knownFresh: true,
                lockKeyPressed: true, lastScanMs: 2000));
        // Same for the other urgent trigger.
        f += Check("NEGATIVE CONTROL: nor does the monster count growing",
            !Program.ShouldScan(haveRecord: false, msSinceScan: 500, knownLockable: 9, knownFresh: true,
                lockableAtLastScan: 1, lastScanMs: 2000));

        // Once the loop has idled as long as the scan took, it may go again:
        // half the wall clock, no worse.
        f += Check("a key press scans once the gap matches the last scan's cost",
            Program.ShouldScan(haveRecord: false, msSinceScan: 2000, knownLockable: 1, knownFresh: true,
                lockKeyPressed: true, lastScanMs: 2000));

        // A cheap scan must not be slowed down by the new floor: the fixed
        // gap still governs when the last scan was quick.
        f += Check("a fast scan still only waits the fixed gap",
            Program.ShouldScan(haveRecord: false, msSinceScan: 400, knownLockable: 1, knownFresh: true,
                lockKeyPressed: true, lastScanMs: 50));
        f += Check("MUTATION: and not a millisecond less",
            !Program.ShouldScan(haveRecord: false, msSinceScan: 399, knownLockable: 1, knownFresh: true,
                lockKeyPressed: true, lastScanMs: 50));

        // A cap on this floor was tried and removed. Capping the wait uncaps
        // the duty: a 15s scan with a 2s gap is 88% of the wall clock in
        // all-core ReadProcessMemory under the game, against 50% uncapped.
        // Stutter is the worse failure, and a scan that slow is a symptom to
        // back off from rather than to wait less after.
        f += Check("NEGATIVE CONTROL: a 15s scan is not repeated after 2s, cap or no cap",
            !Program.ShouldScan(haveRecord: false, msSinceScan: 2000, knownLockable: 1, knownFresh: true,
                lockKeyPressed: true, lastScanMs: 15223));
        f += Check("it waits out the whole cost first",
            Program.ShouldScan(haveRecord: false, msSinceScan: 15223, knownLockable: 1, knownFresh: true,
                lockKeyPressed: true, lastScanMs: 15223));

        // The floor is a floor, not a trigger: with no urgent signal the
        // backoff still has to expire.
        f += Check("NEGATIVE CONTROL: clearing the floor alone does not scan",
            !Program.ShouldScan(haveRecord: false, msSinceScan: 500, knownLockable: 1, knownFresh: true,
                backoffMs: 15000, lastScanMs: 50));

        return f;
    }

    // A region enumerated at the start of the scan can be freed before the
    // scan reaches it, and then every read in it fails.
    //
    // Two ways of handling that are both wrong. Stepping one 4 KiB page per
    // failed syscall made a scan take 15.2 seconds instead of the usual one
    // (measured 2026-09-19: skipped=98416, about 400 MiB walked a page at a
    // time). Striding past it in wider jumps is fast but steps over live
    // memory -- an independent review found the 64 KiB aligned stride that
    // replaced it skipped up to 60 KiB, and the first version of this test
    // missed that because it planted the record exactly on the boundary the
    // stride happened to land on. So the source is asked instead.
    private static int DeadRegionStride()
    {
        int f = 0;
        const int Size = 8 * 1024 * 1024;
        const ulong DeadBytes = 4 * 1024 * 1024;
        const ulong Base = 0x0001_0000;

        // Three placements past the dead run. The first is the boundary the
        // old stride landed on and is the one a careless implementation still
        // passes; the other two sit inside the window it skipped.
        foreach (var (offset, label) in new[]
        {
            (0x40UL, "on the first boundary past it"),
            (0x1040UL, "one page past it"),
            (0xB040UL, "inside the window a 64 KiB stride skipped"),
        })
        {
            ulong recordAt = Base + DeadBytes + offset;
            var mem = new FakeMem(Base, Size);
            var w = mem.Span(recordAt - LockRecord.Before);
            w[..LockRecord.ReadSize].Clear();
            Own(w);
            BinaryPrimitives.WriteUInt64LittleEndian(w[0x08..], MonsterA);
            BinaryPrimitives.WriteDoubleLittleEndian(w[0x20..], -142.5);
            BinaryPrimitives.WriteDoubleLittleEndian(w[0x28..], 311.75);
            BinaryPrimitives.WriteUInt64LittleEndian(w[0x38..], 1UL);

            var known = new KnownEntities("");
            known.Seed(new[] { MonsterA });

            mem.DeadFrom = 0;
            mem.DeadTo = DeadBytes;
            mem.ReadAttempts = 0;
            bool found = LockRecord.Find(mem, known, out string diag).Contains(recordAt);
            f += Check($"a record {label} is found past a freed 4 MiB run (reads={mem.ReadAttempts})", found);

            // NEGATIVE CONTROL: per-page stepping over 4 MiB is 1024 failed
            // reads. Asking the source costs one.
            int perPage = (int)(DeadBytes / 0x1000);
            f += Check($"  and the run is not walked page by page (reads={mem.ReadAttempts} << {perPage})",
                mem.ReadAttempts < 16);
            f += Check($"  and the gap is still reported ({diag})", diag.Contains("skipped="));
        }

        // A source that cannot answer -- a snapshot -- must still find it,
        // by stepping. Slow is acceptable here; wrong is not.
        {
            ulong recordAt = Base + DeadBytes + 0xB040UL;
            var mem = new FakeMem(Base, Size) { CanAnswerNextReadable = false };
            var w = mem.Span(recordAt - LockRecord.Before);
            w[..LockRecord.ReadSize].Clear();
            Own(w);
            BinaryPrimitives.WriteUInt64LittleEndian(w[0x08..], MonsterA);
            BinaryPrimitives.WriteDoubleLittleEndian(w[0x20..], -142.5);
            BinaryPrimitives.WriteDoubleLittleEndian(w[0x28..], 311.75);
            BinaryPrimitives.WriteUInt64LittleEndian(w[0x38..], 1UL);
            var known = new KnownEntities("");
            known.Seed(new[] { MonsterA });
            mem.DeadFrom = 0;
            mem.DeadTo = DeadBytes;
            f += Check("a source that cannot answer still finds it by stepping",
                LockRecord.Find(mem, known, out _).Contains(recordAt));
        }

        // Control: nothing dead, and the walk is a couple of chunk reads.
        {
            ulong recordAt = Base + DeadBytes + 0x40;
            var mem = new FakeMem(Base, Size);
            var w = mem.Span(recordAt - LockRecord.Before);
            w[..LockRecord.ReadSize].Clear();
            Own(w);
            BinaryPrimitives.WriteUInt64LittleEndian(w[0x08..], MonsterA);
            BinaryPrimitives.WriteDoubleLittleEndian(w[0x20..], -142.5);
            BinaryPrimitives.WriteDoubleLittleEndian(w[0x28..], 311.75);
            BinaryPrimitives.WriteUInt64LittleEndian(w[0x38..], 1UL);
            var known = new KnownEntities("");
            known.Seed(new[] { MonsterA });
            mem.ReadAttempts = 0;
            bool ok = LockRecord.Find(mem, known, out _).Contains(recordAt);
            f += Check($"control: found with nothing dead, in {mem.ReadAttempts} reads",
                ok && mem.ReadAttempts <= 8);
        }

        return f;
    }

    // A record adopted in one scene is gone after a zone change, and nothing
    // in the game's memory says so: the owner pointer sits in freed memory
    // and keeps its value, so every check the poll makes still passes.
    private static int SceneInvalidation()
    {
        int f = 0;
        var fresh = new KnownEntities("");
        fresh.Seed(new[] { MonsterA });
        f += Check("a fresh entity list with no scene id yet does not invalidate",
            fresh.SceneId == 0);

        // The exact state of the guild hall failure: list fresh, targets
        // present, gate wide open -- and ShouldScan still refuses, because a
        // record is held. Nothing except the scene id or the trust test can
        // rescue it.
        f += Check("NEGATIVE CONTROL: an open gate does not rescue a held stale record",
            Program.CheckGate(haveRecord: true, fresh) == Program.Gate.Proceed
            && !Program.ShouldScan(haveRecord: true, msSinceScan: 3_600_000,
                knownLockable: 40, knownFresh: true));

        f += Check("the staleness backstop fires within a minute, not ten",
            Program.StaleRecordMsForTests <= 60_000);
        return f;
    }

    // The guild hall, third and actual diagnosis.
    //
    // A scan matches on the uuid in a slot, so it only finds records that
    // hold a lock at that instant. The game keeps the manual lock and the
    // automatic provisional one in separate records; the manual one reads
    // empty while nothing is manually locked. A scan that runs with only an
    // automatic lock present therefore finds the automatic record, the loop
    // calls that "having a record", stops scanning, and every later manual
    // lock lands at an address nobody watches.
    //
    // Measured 2026-09-19, scene 12000: scan found records=2, then
    // auto=0xb30040 held steady for 2m46s with manual never leaving zero,
    // lastValid=0s throughout -- the set was alive, correct and useless.
    // The same hall worked at 19:20 only because the scan there happened to
    // run while a manual lock was held.
    private static int ProvisionalRecordSet()
    {
        int f = 0;

        // NEGATIVE CONTROL: the failure, exactly. A set that has never
        // produced a manual lock, with an automatic lock plainly visible,
        // must not be trusted -- and must therefore not suppress the scan.
        f += Check("NEGATIVE CONTROL: an auto-only set is not trusted",
            !Program.RecordSetTrusted(manualSeen: false, autoUuid: 0xb30040));
        f += Check("NEGATIVE CONTROL: and so it does not suppress scanning",
            Program.ShouldScan(
                haveRecord: Program.RecordSetTrusted(manualSeen: false, autoUuid: 0xb30040),
                msSinceScan: 20_000, knownLockable: 12, knownFresh: true));

        // One manual lock is proof the set holds the right record. After
        // that the old property has to hold again: no timer ever rescans.
        f += Check("a set that produced a manual lock is trusted",
            Program.RecordSetTrusted(manualSeen: true, autoUuid: 0xb30040));
        f += Check("MUTATION: and is never rescanned again, not after an hour",
            !Program.ShouldScan(
                haveRecord: Program.RecordSetTrusted(manualSeen: true, autoUuid: 0xb30040),
                msSinceScan: 3_600_000, knownLockable: 12, knownFresh: true));

        // THE REGRESSION THIS TEST EXISTS FOR. An untrusted set is rescanned,
        // and a scan that succeeds still leaves it untrusted -- so the gap
        // between those scans is the only thing bounding the duty cycle. It
        // was reset to RescanMs on every success, giving scan(~1s) +
        // gap(~1s) for as long as a player fought without manually locking:
        // a permanent 50%, worse than the 45% this work removed. Caught by
        // independent review before it shipped.
        //
        // The backoff now grows on every scan and only a manual lock resets
        // it, so the duty falls away geometrically instead of holding.
        long backoff = Program.BackoffAfterManualLock();
        long worstDutyNumerator = 0, worstDutyDenominator = 0;
        for (int i = 0; i < 8; i++)
        {
            const long scanCost = 1000;
            long gap = Math.Max(400, scanCost);
            gap = Math.Max(gap, backoff);
            worstDutyNumerator += scanCost;
            worstDutyDenominator += scanCost + gap;
            backoff = Program.BackoffAfterScan((int)backoff);
        }
        int dutyPercent = (int)(100 * worstDutyNumerator / worstDutyDenominator);
        f += Check($"untrusted rescanning settles below a third of the wall clock ({dutyPercent}%)",
            dutyPercent < 33);

        // An empty slot is left alone. Nothing is locked, so there is no
        // manual record to find and a scan would match nothing -- rescanning
        // here is how the scanner got back to a 45% duty cycle before.
        f += Check("an idle set with nothing locked stays trusted",
            Program.RecordSetTrusted(manualSeen: false, autoUuid: 0));
        f += Check("MUTATION: so standing around does not restart scanning",
            !Program.ShouldScan(
                haveRecord: Program.RecordSetTrusted(manualSeen: false, autoUuid: 0),
                msSinceScan: 3_600_000, knownLockable: 12, knownFresh: true));

        return f;
    }

    // A diagnostic that cannot fire is worse than no diagnostic: its silence
    // reads as evidence that the state never happened, and three diagnoses of
    // the guild hall defect were wrong partly because of exactly that.
    //
    // The gate line shipped behind `new Stopwatch()`, which is not running.
    // ElapsedMilliseconds stays at zero, the >= threshold never passes, and
    // the Restart() that would have started it is inside the branch it gates.
    // The log from the run that caught the defect contains no gate line at
    // all, across 27 seconds of a shut gate in a town.
    private static int DiagnosticsCanFire()
    {
        int f = 0;

        // NEGATIVE CONTROL: the shipped construction, in isolation.
        var unstarted = new System.Diagnostics.Stopwatch();
        System.Threading.Thread.Sleep(5);
        f += Check("NEGATIVE CONTROL: an unstarted Stopwatch never reaches a threshold",
            unstarted.ElapsedMilliseconds == 0 && !unstarted.IsRunning);

        var started = System.Diagnostics.Stopwatch.StartNew();
        f += Check("a started one does", started.IsRunning);

        // The throttles have to be short enough to catch a transient state
        // and long enough not to fill the log while a player stands in a town.
        f += Check("the gate throttle is between a second and a minute",
            Program.GateLogMsForTests is > 1000 and <= 60_000);
        f += Check("the idle throttle is too",
            Program.IdleLogMsForTests is > 1000 and <= 60_000);

        return f;
    }

    // The fix that removed 89 of the 90 scans.
    private static int TimerRescan()
    {
        int f = 0;

        // NEGATIVE CONTROL. The old policy says yes here whether or not a
        // lock was held -- 15 s with, 3 s without -- and that is what spent
        // 45% of a session scanning. An hour covers both thresholds.
        f += Check("NEGATIVE CONTROL: a known record is never rescanned, not after an hour",
            !Program.ShouldScan(haveRecord: true, msSinceScan: 3_600_000, knownLockable: 400, knownFresh: true));
        // Weaker: the 3 s threshold only applied with no lock held, and this
        // predicate has no argument for that, so this is a mutation check.
        f += Check("MUTATION: nor after three seconds",
            !Program.ShouldScan(haveRecord: true, msSinceScan: 3_000, knownLockable: 400, knownFresh: true));

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
            bystanders.Count == 8 && bystanders.LockableCount == 0);
        f += Check("so it does not justify a scan",
            !Program.ShouldScan(haveRecord: false, msSinceScan: long.MaxValue,
                knownLockable: bystanders.LockableCount, knownFresh: true));
        // MUTATION, not a control: it asserts true, and the old policy said
        // true for every haveRecord:false case. What it guards is the call
        // site passing Known.Count instead of Known.LockableCount -- a live
        // hazard, since that swap was made once and shipped -- but only
        // GateOrder below actually runs the call site.
        f += Check("MUTATION: gating on the entity count would scan anyway",
            Program.ShouldScan(haveRecord: false, msSinceScan: long.MaxValue,
                knownLockable: bystanders.Count, knownFresh: true));

        var withMonster = new KnownEntities("");
        withMonster.Seed(new ulong[] { 0xa8dbf60280, MonsterA });
        f += Check("one monster in the list is enough",
            withMonster.LockableCount == 1
            && Program.ShouldScan(haveRecord: false, msSinceScan: long.MaxValue,
                knownLockable: withMonster.LockableCount, knownFresh: true));

        f += Check("no scan without an entity list",
            !Program.ShouldScan(haveRecord: false, msSinceScan: long.MaxValue, knownLockable: 0, knownFresh: true));
        f += Check("no scan with a stale entity list",
            !Program.ShouldScan(haveRecord: false, msSinceScan: long.MaxValue, knownLockable: 400, knownFresh: false));

        f += Check("the first scan does not wait out the backoff",
            Program.ShouldScan(haveRecord: false, msSinceScan: long.MaxValue, knownLockable: 400, knownFresh: true));
        f += Check("a failed scan backs off before retrying",
            !Program.ShouldScan(haveRecord: false, msSinceScan: 100, knownLockable: 400, knownFresh: true));
        f += Check("and retries once the backoff is up",
            Program.ShouldScan(haveRecord: false, msSinceScan: 5_000, knownLockable: 400, knownFresh: true));

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
        // MUTATION: delete the `knownLockable > lockableAtLastScan` clause
        // and this fails. Not a control against 9de20b4, which had no backoff
        // for it to beat.
        f += Check("MUTATION: the list going from 4 monsters to 11 retries at once",
            Program.ShouldScan(haveRecord: false, msSinceScan: 600, knownLockable: 11, knownFresh: true,
                lockableAtLastScan: 4));
        f += Check("a list that has not grown does not",
            !Program.ShouldScan(haveRecord: false, msSinceScan: 600, knownLockable: 4, knownFresh: true,
                lockableAtLastScan: 4));

        // The lock key. Pressing it means a lock is being held right now,
        // which is the only state the scan can succeed in.
        f += Check("the lock key starts a scan immediately",
            Program.ShouldScan(haveRecord: false, msSinceScan: 500, knownLockable: 4, knownFresh: true,
                lockableAtLastScan: 4, lockKeyPressed: true));
        f += Check("but not while a record is already known",
            !Program.ShouldScan(haveRecord: true, msSinceScan: 500, knownLockable: 4, knownFresh: true,
                lockableAtLastScan: 4, lockKeyPressed: true));
        f += Check("and not without monsters to match",
            !Program.ShouldScan(haveRecord: false, msSinceScan: 500, knownLockable: 0, knownFresh: true,
                lockableAtLastScan: 0, lockKeyPressed: true));

        // Neither urgent trigger may turn into a scan loop.
        f += Check("a held key cannot scan faster than the floor",
            !Program.ShouldScan(haveRecord: false, msSinceScan: 50, knownLockable: 4, knownFresh: true,
                lockableAtLastScan: 4, lockKeyPressed: true));
        f += Check("nor can a list growing one entity at a time",
            !Program.ShouldScan(haveRecord: false, msSinceScan: 50, knownLockable: 12, knownFresh: true,
                lockableAtLastScan: 11));
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

        // It must not take back anything it cannot confirm, and "confirm" has
        // two forms that this test used to run together.
        //
        // Without an owner on record -- a fresh process, or a scene change
        // that cleared them -- the lock itself is the only evidence there is,
        // and the strict rule applies. Each of these gets its own empty
        // dictionary because a successful re-adoption records the owner and
        // would hand the next check the very evidence it is meant to lack.
        Clear(mem, after);
        f += Check("NEGATIVE CONTROL: a cleared slot with no owner on record is not re-adopted",
            Program.Readopt(mem, sticky, known, scratch, new Dictionary<ulong, ulong>()).Count == 0);

        var stranger = new KnownEntities("");
        stranger.Seed(new[] { MonsterB });
        Set(mem, MonsterA, manual: true);
        f += Check("NEGATIVE CONTROL: a uuid the packet side does not name is not re-adopted",
            Program.Readopt(mem, sticky, stranger, scratch, new Dictionary<ulong, ulong>()).Count == 0);

        BinaryPrimitives.WriteUInt64LittleEndian(mem.Span(SlotUuidAddr), 0xa8dbf60280);
        f += Check("NEGATIVE CONTROL: a player in the slot is not re-adopted",
            Program.Readopt(mem, sticky, known, scratch, new Dictionary<ulong, ulong>()).Count == 0);

        f += Check("an address that reads as nothing is not re-adopted",
            Program.Readopt(mem, new ulong[] { 0x9000_0000_0000 }, known, scratch, owners).Count == 0);

        // An owner already on record is the other form, and demanding a lock
        // on top of it is the measured defect. The poll keeps an address
        // whose owner is unchanged whether or not anything is locked in it --
        // that is its stated retention rule -- and re-adoption asking a
        // stricter question about the same address is the inconsistency that
        // cost two of three records at 20:22:45 on 2026-09-19.
        Clear(mem, after);
        var remembered = new Dictionary<ulong, ulong> { [SlotUuidAddr] = after };
        f += Check("an empty record IS taken back when the owner is the one on record",
            Program.Readopt(mem, sticky, known, scratch, remembered).Contains(SlotUuidAddr));

        // The protection that matters is still there: a different object now
        // living at the address is not the record, however well it parses.
        var wrongOwner = new Dictionary<ulong, ulong> { [SlotUuidAddr] = Owner + 0x99000 };
        f += Check("NEGATIVE CONTROL: an empty record under a different owner is not taken back",
            Program.Readopt(mem, sticky, known, scratch, wrongOwner).Count == 0);
        return f;
    }

    // The record set must be able to grow back.
    //
    // A scan matches on the uuid sitting in a slot, so it only ever sees the
    // records that hold a lock at that instant, and the game keeps several --
    // six at once on 2026-09-19 at 15:00:03. Any single scan or re-adoption
    // therefore returns a subset, and the loop has to keep asking for the
    // rest. It did the opposite: it narrowed to whatever the last pass
    // returned and never widened.
    //
    // The measured run, from lock-events.log:
    //
    //   20:19:32.065  scan rec=3                   three records remembered
    //   20:22:45.420  back without a scan (of 3)   one re-adopted, two dropped
    //   20:22:45.42x  sticky := records            the memory of the other two gone
    //   20:22:45-20:23:15                          locks land, then stop landing
    //   20:23:15-20:24:15  overlay "-"             60 s, seventeen monsters in view
    //
    // and no scan line anywhere after 20:22:45, because the set had produced
    // a manual lock once and so counted as trusted for good.
    private static int RecordSetWidens()
    {
        int f = 0;

        // Remembering is a union. The call sites used to assign, which is
        // what turned a memory of three addresses into a memory of one.
        var sticky = new List<ulong>();
        Program.Remember(sticky, new ulong[] { 0x1000, 0x2000, 0x3000 });
        f += Check("a scan's records are remembered", sticky.Count == 3);

        Program.Remember(sticky, new ulong[] { 0x2000 });
        f += Check("NEGATIVE CONTROL: re-adopting one of them does not forget the others",
            sticky.Count == 3 && sticky.Contains(0x1000) && sticky.Contains(0x3000));

        Program.Remember(sticky, new ulong[] { 0x4000, 0x2000 });
        f += Check("a newly found address is added, a known one is not duplicated",
            sticky.Count == 4 && sticky.FindAll(a => a == 0x2000).Count == 1);

        // Bounded, so a process that churns addresses cannot grow it without
        // limit. Oldest out first.
        var capped = new List<ulong>();
        for (ulong i = 1; i <= 10; i++) Program.Remember(capped, new[] { i * 0x1000 }, cap: 4);
        f += Check("the memory is capped and drops the oldest",
            capped.Count == 4 && capped[0] == 0x7000 && capped[3] == 0xa000);

        // And the whole point: two records, only one of them holding a lock
        // when the pass runs, and both still in the set afterwards.
        var mem = new FakeMem(0x0001_0000, 0x0010_0000);
        var scratch = new byte[LockRecord.ReadSize];
        var known = new KnownEntities("");
        known.Seed(new[] { MonsterA, MonsterB });
        ulong second = SlotUuidAddr + 0x1000;
        var owners = new Dictionary<ulong, ulong>();

        // Both are real records; the manual one is empty, as it is whenever
        // nothing is manually locked, and the automatic one holds a monster.
        Set(mem, MonsterA, manual: false);
        SetAt(mem, second, 0, manual: true);
        var both = new[] { SlotUuidAddr, second };

        var firstPass = Program.Readopt(mem, both, known, scratch, owners);
        f += Check("premise: one pass sees only the record that holds a lock",
            firstPass.Count == 1 && firstPass[0] == SlotUuidAddr);

        // The empty one is confirmed the moment it holds a lock once, and
        // from then on its owner is the evidence.
        SetAt(mem, second, MonsterB, manual: true);
        Program.Readopt(mem, both, known, scratch, owners);
        SetAt(mem, second, 0, manual: true);
        var again = Program.Readopt(mem, both, known, scratch, owners);
        f += Check("THE DEFECT: an empty manual record stays in the set once confirmed",
            again.Count == 2);

        // Trust has to be able to lapse, or a set that stopped receiving
        // manual locks holds the scanner shut for the life of the process.
        // One ACQUIRING press is enough now. A press made while a target was
        // already on screen is a clear or a switch and is not counted at all,
        // which is what the second press used to stand in for -- badly:
        // measured 2026-09-19, waiting for it cost 6.5 seconds of "-" that
        // the player ended by pressing again themselves.
        f += Check("NEGATIVE CONTROL: trust lapses on one unanswered acquiring press",
            Program.DistrustAfterUnansweredPresses(manualSeen: true, unanswered: 1));
        f += Check("  a set that never earned trust still has none to lose",
            !Program.DistrustAfterUnansweredPresses(manualSeen: false, unanswered: 1));

        // A whiff -- lock pressed with nothing in range -- is indistinguishable
        // from a miss, so distrust has to be affordable when every press is a
        // whiff. The floor is what makes it so.
        long wbackoff = Program.BackoffAfterUnansweredPress(Program.BackoffAfterManualLock());
        long wnum = 0, wden = 0;
        for (int i = 0; i < 8; i++)
        {
            const long scanCost = 1000;
            long gap = Math.Max(Math.Max(400, scanCost), wbackoff);
            wnum += scanCost;
            wden += scanCost + gap;
            // Every whiff is answered by a scan, and every scan is followed by
            // a manual lock that resets the backoff -- the worst case.
            wbackoff = Program.BackoffAfterUnansweredPress(Program.BackoffAfterManualLock());
        }
        int wduty = (int)(100 * wnum / wden);
        f += Check($"NEGATIVE CONTROL: whiffing every press stays near a third of the clock ({wduty}%)",
            wduty <= 34);
        f += Check("and a set that never earned trust has none to lose",
            !Program.DistrustAfterUnansweredPresses(manualSeen: false, unanswered: 9));

        // The crash. The staleness drop empties the set inside the loop body,
        // and this line runs below it.
        f += Check("THE CRASH: the idle diagnostic survives an empty set",
            Program.IdleLine(new List<ulong>(), 3, 7, 18, 17, 60_000, 0, false)
                   .Contains("records=0/3"));
        f += Check("and still names the address when there is one",
            Program.IdleLine(new List<ulong> { 0x132e47af950 }, 3, 7, 18, 17, 30_000, 0, true)
                   .Contains("rec=0x132e47af950"));

        // Why it fired every time rather than occasionally: the two clocks
        // are restarted by the same event, so the 60 s expiry always lands on
        // a pass where the 30 s line is due.
        f += Check("premise: the staleness timer expires on an idle-line pass",
            Program.StaleRecordMsForTests % Program.IdleLogMsForTests == 0);

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
            bystanders.Count == 8 && bystanders.LockableCount == 0
            && Program.CheckGate(haveRecord: false, bystanders) == Program.Gate.NoTargets);

        var withMonster = new KnownEntities("");
        withMonster.Seed(new ulong[] { 0xa8dbf60280, MonsterA });
        f += Check("one monster in the list opens it",
            Program.CheckGate(haveRecord: false, withMonster) == Program.Gate.Proceed);

        // The guild hall's training dummies. Reported 2026-09-19 as never
        // appearing on the overlay, which led to a guess that they were
        // EEntityType EntDummy and a change admitting that type. They are
        // not: these are the real uuids, cached under "Enemy Training
        // Dummy", "Elite Enemy Training Dummy" and "Elite Guardian Dummy",
        // and all three are plain EntMonster. All three were confirmed
        // locked and published once the scan stopped taking fifteen seconds.
        foreach (var (dummy, label) in new[]
        {
            (0x460040UL, "Enemy Training Dummy"),
            (0x4b0040UL, "Elite Enemy Training Dummy"),
            (0xb30040UL, "Elite Guardian Dummy"),
        })
        {
            var hall = new KnownEntities("");
            hall.Seed(new ulong[] { 0xa8dbf60280, dummy });
            f += Check($"the guild hall's {label} is lockable and opens the gate",
                UuidShape.IsLockable(dummy) && hall.LockableCount == 1
                && Program.CheckGate(haveRecord: false, hall) == Program.Gate.Proceed);
        }

        // NEGATIVE CONTROL, and the reason the guess was harmful. EntDummy
        // (11) is not a training dummy: 722 of the 733 in the name cache
        // carry the summon bit and they are skill effects -- lightning,
        // meteors, arrow rain, damage proxies. Admitting them opens the gate
        // wherever anyone is casting, which is everywhere, and the gate is
        // the only thing keeping the scanner off a 45% duty cycle.
        const ulong SkillEffect = 0x9a26382c0; // real, from the name cache
        f += Check("NEGATIVE CONTROL: a type-11 skill effect is NOT lockable",
            !UuidShape.IsLockable(SkillEffect) && UuidShape.TypeOf(SkillEffect) == UuidShape.EntDummy);
        var casting = new KnownEntities("");
        casting.Seed(new ulong[] { 0xa8dbf60280, SkillEffect });
        f += Check("NEGATIVE CONTROL: and does not open the gate on its own",
            casting.LockableCount == 0
            && Program.CheckGate(haveRecord: false, casting) == Program.Gate.NoTargets);
        f += Check("NEGATIVE CONTROL: a player is still not lockable",
            !UuidShape.IsLockable(0xa8dbf60280));

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

    // Polling a known address must not re-run the discovery test.
    //
    // The scan signature exists to pick one address out of gigabytes: owner
    // pointer, three zero qwords, two plausible doubles, kind 0 or 1. Some of
    // that is incidental to what a record IS -- it is there to make a
    // needle-in-a-haystack match selective. The poll ran the same test on an
    // address it had already identified, and treated a rejection as "nothing
    // is locked": silent, per-target, and cured by locking something else.
    // That is the reported symptom exactly.
    //
    // The retention rule was always meant to be the owner pointer -- the file
    // says so about the uuid ("Parse cannot serve here: it is the discovery
    // test") -- and the poll simply never followed it.
    private static int PollIsNotDiscovery()
    {
        int f = 0;
        var mem = new FakeMem(0x0001_0000, 0x0010_0000);
        var scratch = new byte[LockRecord.ReadSize];

        // A real manual lock, except that one of the incidental fields does
        // not match the signature. The value is measured: the snapshot corpus
        // contains this heap pointer sitting in a position field, and the
        // signature rejects it under both the old rule and the corrected one.
        // (The fixture used to be the double 0.5, which the corrected
        // signature accepts -- it reads as the float pair 0 / 1.75 -- so it
        // stopped demonstrating anything.)
        Set(mem, MonsterA, manual: true);
        BinaryPrimitives.WriteUInt64LittleEndian(
            mem.Span(SlotUuidAddr - LockRecord.Before)[0x20..], 0x0000024bcd0ca720UL);
        var window = mem.Span(SlotUuidAddr - LockRecord.Before)[..LockRecord.ReadSize];

        // Premise, and the negative control in one: this is what the poll used
        // to see, and it read as an empty slot.
        f += Check("NEGATIVE CONTROL: the scan signature rejects this record",
            !LockRecord.Parse(window).Valid);
        f += Check("  and the reason is named in the log line",
            LockRecord.Explain(window).Contains("posA="));

        var raw = LockRecord.ReadRaw(window);
        f += Check("THE DEFECT: the target is right there in the record",
            raw.Uuid == MonsterA && raw.KindWord == 1);
        f += Check("  so the poll takes it", Program.PollAccepts(raw));

        // And re-adoption, which had the same conflation.
        var known = new KnownEntities("");
        known.Seed(new[] { MonsterA, MonsterB });
        var owners = new Dictionary<ulong, ulong> { [SlotUuidAddr] = Owner };
        f += Check("  and re-adoption keeps it too",
            Program.Readopt(mem, new[] { SlotUuidAddr }, known, scratch, owners)
                   .Contains(SlotUuidAddr));

        // What still has to hold. Relaxing the shape test must not turn the
        // poll into "read whatever is there".
        Set(mem, MonsterA, manual: true);
        BinaryPrimitives.WriteUInt64LittleEndian(
            mem.Span(SlotUuidAddr - LockRecord.Before)[0x38..], 2);
        f += Check("NEGATIVE CONTROL: a kind field that is neither lock kind is not read",
            !Program.PollAccepts(LockRecord.ReadRaw(
                mem.Span(SlotUuidAddr - LockRecord.Before)[..LockRecord.ReadSize])));

        Set(mem, 0xa8dbf60280, manual: true);
        f += Check("NEGATIVE CONTROL: a player in the slot is not read",
            !Program.PollAccepts(LockRecord.ReadRaw(
                mem.Span(SlotUuidAddr - LockRecord.Before)[..LockRecord.ReadSize])));

        Clear(mem, Owner);
        f += Check("NEGATIVE CONTROL: an empty slot is still empty",
            !Program.PollAccepts(LockRecord.ReadRaw(
                mem.Span(SlotUuidAddr - LockRecord.Before)[..LockRecord.ReadSize])));

        // The owner pointer is the whole of the retention rule now, so it has
        // to be a pointer. A remembered owner that is garbage must not let a
        // run of garbage match itself.
        Set(mem, MonsterA, manual: true);
        BinaryPrimitives.WriteUInt64LittleEndian(
            mem.Span(SlotUuidAddr - LockRecord.Before), 0x41);
        var junkOwner = new Dictionary<ulong, ulong> { [SlotUuidAddr] = 0x41 };
        f += Check("NEGATIVE CONTROL: a remembered owner that is not a pointer is not honoured",
            Program.Readopt(mem, new[] { SlotUuidAddr }, known, scratch, junkOwner).Count == 0);

        return f;
    }

    // The field the layout called "target position (double)".
    //
    // Every value below was measured, none was constructed to satisfy the
    // predicate. The first seven are the two position fields of real lock
    // records: five from the snapshot corpus under
    // records/20260919-1224/snapshots, two from the live helper log at
    // 20:55:28 and 20:55:38 on 2026-09-19. The last is a heap pointer that
    // the corpus really does contain in one of these fields, and is the
    // control that keeps the predicate from being a no-op.
    //
    // Measured on the production path (LockRecord.Find over the eight
    // snapshots, entity filter on, 2026-09-19):
    //
    //              records found     of which manual
    //   before          14                  0
    //   after           18                  4
    //
    // Four of four manual records were invisible to the scan, and the four
    // the change adds are exactly those. No other record appeared.
    private static int PositionFieldIsAFloatPair()
    {
        int f = 0;

        // Field B of a manual record: (99.5147f, 0.0f). Read as a double it
        // is 3.5e-314, which is how the old test saw it.
        const ulong ManualB = 0x0000000042c7078bUL;
        // The same field on a different sweep: (99.3531f, -1.0f).
        const ulong ManualBNeg = 0xbf80000042c6b4d0UL;
        // Live, a different map: (143.393f, 0.5082f) and (143.257f, 0.4286f).
        const ulong LiveB1 = 0x3f021c68430f649aUL;
        const ulong LiveB2 = 0x3edb7294430f41c9UL;
        // Field B of an automatic record: (99.3532f, 3.84701f). This one the
        // old test happened to accept, which is why auto locks always worked.
        const ulong AutoB = 0x4076356c42c6b4d1UL;
        // Field A, which reads as a plain double in every sample.
        const ulong FieldA = 0xc0e0000000000000UL;
        // The control: a heap pointer sitting in one of these fields.
        const ulong Pointer = 0x0000024bcd0ca720UL;

        f += Check("NEGATIVE CONTROL: the old test rejects a real manual record's field",
            !Scanner.Doubleish(ManualB));
        f += Check("NEGATIVE CONTROL: and the other measured manual values too",
            !Scanner.Doubleish(ManualBNeg) && !Scanner.Doubleish(LiveB1) && !Scanner.Doubleish(LiveB2));
        f += Check("premise: it accepted the automatic record's, which is why auto always worked",
            Scanner.Doubleish(AutoB));

        f += Check("THE FIX: a manual record's field is a pair of float32s and is accepted",
            Scanner.PositionField(ManualB));
        f += Check("  and so are the other three measured manual values",
            Scanner.PositionField(ManualBNeg) && Scanner.PositionField(LiveB1)
            && Scanner.PositionField(LiveB2));
        f += Check("  the automatic record is still accepted",
            Scanner.PositionField(AutoB));
        f += Check("  field A, which really is a double, is still accepted",
            Scanner.PositionField(FieldA));
        f += Check("  and an empty record still is",
            Scanner.PositionField(0));

        f += Check("NEGATIVE CONTROL: a heap pointer in the field is still rejected",
            !Scanner.PositionField(Pointer));

        // The same mistake in the three qwords the layout calls "0". Measured
        // twice, identically, at two record addresses in two sessions:
        // 21:21:18.636 and 21:43:08.116. Read as int32 pairs they are flags.
        f += Check("NEGATIVE CONTROL: the strict test rejects a record whose flags are set",
            0x100000001UL != 0);
        f += Check("THE FIX: an int32 flag pair is accepted where the layout said zero",
            Scanner.ZeroOrFlagPair(0x100000001UL) && Scanner.ZeroOrFlagPair(0x100000000UL));
        f += Check("  and a plain zero still is", Scanner.ZeroOrFlagPair(0));
        f += Check("NEGATIVE CONTROL: anything past a flag is not",
            !Scanner.ZeroOrFlagPair(2) && !Scanner.ZeroOrFlagPair(0x200000000UL)
            && !Scanner.ZeroOrFlagPair(0x0000024bcd0ca720UL));

        // End to end, from the measured window.
        var flagged = new FakeMem(0x0001_0000, 0x0010_0000);
        Set(flagged, MonsterA, manual: true);
        var fw = flagged.Span(SlotUuidAddr - LockRecord.Before);
        BinaryPrimitives.WriteUInt64LittleEndian(fw[0x10..], 0x100000001UL);
        BinaryPrimitives.WriteUInt64LittleEndian(fw[0x18..], 0x100000000UL);
        BinaryPrimitives.WriteUInt64LittleEndian(fw[0x30..], 0x100000001UL);
        f += Check("THE DEFECT: a record with its flags set is found",
            LockRecord.Parse(fw[..LockRecord.ReadSize]).LockKind == LockRecord.Kind.Manual);
        f += Check("NEGATIVE CONTROL: so is a pair of NaNs",
            !Scanner.PositionField(0x7fc000007fc00000UL));
        f += Check("NEGATIVE CONTROL: and a pair of denormals",
            !Scanner.PositionField(0x0000000100000001UL));

        // And end to end: the whole record parses, with the right kind.
        var mem = new FakeMem(0x0001_0000, 0x0010_0000);
        Set(mem, MonsterA, manual: true);
        var w = mem.Span(SlotUuidAddr - LockRecord.Before);
        BinaryPrimitives.WriteUInt64LittleEndian(w[0x20..], FieldA);
        BinaryPrimitives.WriteUInt64LittleEndian(w[0x28..], ManualB);
        var view = LockRecord.Parse(w[..LockRecord.ReadSize]);
        f += Check("THE DEFECT: a manual record built from measured bytes is found",
            view.Valid && view.Uuid == MonsterA && view.LockKind == LockRecord.Kind.Manual);

        return f;
    }

    // Set(), but at an arbitrary address, so a second record can exist.
    private static void SetAt(FakeMem mem, ulong uuidAddr, ulong uuid, bool manual)
    {
        var w = mem.Span(uuidAddr - LockRecord.Before);
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

        // Models a region that was freed or re-protected under the scan.
        // ReadProcessMemory fails for a range that touches it at all, which
        // is why one bad page rejects a whole 4 MiB chunk request.
        public ulong DeadFrom;
        public ulong DeadTo;
        public int ReadAttempts;

        public bool Read(ulong addr, byte[] buffer, int length, out int read)
        {
            read = 0;
            ReadAttempts++;
            if (addr < baseAddr) return false;
            ulong off = addr - baseAddr;
            if (off >= (ulong)bytes.Length) return false;
            read = (int)Math.Min((ulong)length, (ulong)bytes.Length - off);
            if (DeadTo > DeadFrom && off < DeadTo && off + (ulong)read > DeadFrom)
            {
                read = 0;
                return false;
            }
            Array.Copy(bytes, (int)off, buffer, 0, read);
            return true;
        }

        // Set false to model a source that cannot answer (a snapshot), so the
        // caller's page-stepping fallback is exercised too.
        public bool CanAnswerNextReadable = true;

        public bool TryNextReadable(ulong addr, out ulong next)
        {
            next = 0;
            if (!CanAnswerNextReadable) return false;
            if (DeadTo <= DeadFrom) return false;
            ulong off = addr < baseAddr ? 0 : addr - baseAddr;
            if (off >= DeadTo) return false;
            next = baseAddr + DeadTo;
            return true;
        }

        public string Describe => "fake";
    }
}
