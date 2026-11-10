using Xunit;

// Test klase jedna za drugom: ZLG/NCU24L testovi mere vreme odgovora (desetine ms), a paralelno sa ~140 drugih testova
// (POS engine, pty pumpe, procesi) su na CI mašini padali (10.11.2026). Ceo projekat traje ~20–40 s.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
