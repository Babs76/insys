# Results owed from 30 October

CI results of earlier batches, re-read from GitHub's logs on 31 October. They stayed off those branches so their heads wouldn't move. All jobs passed on each head; none has a failed test.

| Batch | Head | Unit and architecture (passed, skipped) | Integration shards | Sweeps | General e2e, per date | Life/Health/Enterprise e2e, per date |
|---|---|---|---|---|---|---|
| Ledger fix | a95bace | 1593, 10 | 101, 151, 156, 170 | 33, 25 | 24 | 25 |
| Accumulator fix | 025beb2 | 1594, 10 | 101, 151, 156, 171 | 33, 26 | 24 | 26 |
| 5a.1 | d43128b | 1607, 10 | 149, 151, 139, 140 | 33, 27 | 24 | 27 |
| BR-116 (counts lost at the time) | abebdf8 | 1622, 10 | 115, 198, 141, 129 | 33, 28 | 24 | 28 |
| 5a.2 | 1d38b89 | 1633, 10 | 116, 200, 141, 129 | 33, 31 | 24 | 31 |
| 5a.3 | 59a7f15 | 1646, 10 | 142, 145, 188, 111 | 33, 32 | 24 | 32 |

Shard sizes move between runs because the plan balances them by timing.

**Still owed:** the full locked suite on BR-116, BR-117 and the Communications fix has never run. CI on each exact head stands in for it, and the front end has held the lock most of the time since.
