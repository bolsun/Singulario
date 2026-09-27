using System;
using System.Collections.Generic;
using System.Linq;

namespace Singulario.Core;

// The whole tick, in the 4 steps from the porting spec, in order:
// 1. rotation, 2. capture (source + star items), 3. transfer between
// neighbors, 4. delivery (singularity / black hole / star).
//
// Deliberately NOT ported from the original prototype (see the spec's
// "What NOT to port" section) -- these are not present anywhere below,
// not even as disabled options:
//   - "requireLowerTotal" (total-charge comparison gate on transfer)
//   - "requireLowerColorTotal" (per-color comparison gate on transfer)
//   - "intraRingDiffusion" (45-degree same-ring hopping)
//   - "diagonalTransfer" (diagonal capture/transfer) -- capture and
//     transfer here ONLY ever consider orthogonal (N/E/S/W) directions,
//     hardcoded, not a toggle.
// The only anti-oscillation rule that remains is RingSlot.Locked (a
// freshly-arrived charge can't be given away again until this nucleus's
// ring rotates at least once) -- see the transfer step below. Without the
// two removed comparison gates, closed loops of nuclei CAN circulate charge
// indefinitely without ever reaching the singularity; that's an accepted
// simplification, not a bug, per the spec.
public static class Simulation
{
    // Ring index 0..7 = N, NE, E, SE, S, SW, W, NW (45 degree steps
    // clockwise from north), matching Nucleus.Ring's layout.
    public static readonly (int DR, int DC)[] Adj8 =
    {
        (-1, 0), (-1, 1), (0, 1), (1, 1), (1, 0), (1, -1), (0, -1), (-1, -1),
    };

    static int Opposite(int i) => (i + 4) % 8;

    // Indices 0,2,4,6 (N/E/S/W) are orthogonal; 1,3,5,7 (NE/SE/SW/NW) are
    // diagonal. Capture and transfer only ever use the orthogonal ones.
    static bool IsOrthogonal(int i) => i % 2 == 0;

    static bool IsItemColor(string color) => color != null && color.StartsWith("nucleus:");

    public static void Tick(GameWorld w)
    {
        w.TickCounter++;
        var nuclei = new List<Nucleus>(w.Nuclei.Values);

        RotateDueNuclei(w, nuclei);
        CaptureStep(w, nuclei);
        var transfers = PlanTransfers(w, nuclei);
        ApplyTransfers(transfers);
        DeliveryStep(w, nuclei);
    }

    // ---- step 1: rotation ----
    static void RotateDueNuclei(GameWorld w, List<Nucleus> nuclei)
    {
        foreach (var n in nuclei)
        {
            if (w.TickCounter % w.NucleusTicks(n) == 0)
                Rotate(n);
        }
    }

    static void Rotate(Nucleus n)
    {
        var old = n.Ring;
        var next = new RingSlot[8];
        for (int i = 0; i < 8; i++)
        {
            int srcIdx = ((i - n.Dir) % 8 + 8) % 8;
            var src = old[srcIdx];
            // A fresh copy clears Locked -- physically rotating a charge
            // into a new position is what "unlocks" it.
            next[i] = src == null ? null : new RingSlot(src.IsHole, src.Color, src.Charge, false);
        }
        n.Ring = next;
    }

    // ---- step 2: capture from a source or a star (holes only, orthogonal
    // only, gated by the shared per-nucleus capture cooldown) ----
    static void CaptureStep(GameWorld w, List<Nucleus> nuclei)
    {
        foreach (var n in nuclei)
        {
            for (int k = 0; k < 8; k++)
            {
                var slot = n.Ring[k];
                if (slot == null || !slot.IsHole) continue;
                if (!IsOrthogonal(k)) continue;

                if (w.Config.CaptureCooldownEnabled && n.LastCaptureTick.HasValue
                    && (w.TickCounter - n.LastCaptureTick.Value) < w.Config.CaptureCooldownTicks)
                    continue;

                int nr = n.Row + Adj8[k].DR;
                int nc = n.Col + Adj8[k].DC;
                if (!w.InBounds(nr, nc)) continue;

                var cell = w.Grid[nr, nc];
                if (cell.Source != null && cell.Source.Remaining > 0)
                {
                    float amt = Math.Min(w.Config.BaseYield, cell.Source.Remaining);
                    if (amt <= 0) continue;
                    slot.IsHole = false;
                    slot.Color = cell.Source.Color;
                    slot.Charge = amt;
                    slot.Locked = true;
                    n.LastCaptureTick = w.TickCounter;
                    cell.Source.Remaining -= amt;
                    if (cell.Source.Remaining <= 0) cell.Source = null;
                    continue;
                }

                var starId = w.StarAt[nr, nc];
                if (starId.HasValue)
                {
                    var star = w.Stars[starId.Value];
                    if (star.ReadyCount > 0)
                    {
                        slot.IsHole = false;
                        slot.Color = "nucleus:" + star.Color;
                        slot.Charge = 1f;
                        slot.Locked = true;
                        n.LastCaptureTick = w.TickCounter;
                        star.ReadyCount -= 1;
                    }
                }
            }
        }
    }

    // ---- step 3: transfer between touching nuclei (orthogonal only,
    // unconditional other than the Locked check) ----
    // Plans transfers: for every hole in a nucleus's ring facing an
    // orthogonal neighbor, if that neighbor has a charged (non-hole,
    // unlocked) slot facing back, queue a transfer.
    static List<(Nucleus N, int GiveIndex, Nucleus M, int ReceiveIndex)> PlanTransfers(GameWorld w, List<Nucleus> nuclei)
    {
        var transfers = new List<(Nucleus, int, Nucleus, int)>();
        foreach (var n in nuclei)
        {
            for (int k = 0; k < 8; k++)
            {
                var slot = n.Ring[k];
                if (slot == null || !slot.IsHole) continue;
                if (!IsOrthogonal(k)) continue;

                int nr = n.Row + Adj8[k].DR;
                int nc = n.Col + Adj8[k].DC;
                if (!w.InBounds(nr, nc)) continue;

                var mid = w.EntityAt[nr, nc];
                if (!mid.HasValue) continue;
                var m = w.Nuclei[mid.Value];
                int j = Opposite(k);
                var giveSlot = m.Ring[j];
                if (giveSlot == null || giveSlot.IsHole || giveSlot.Locked) continue;

                transfers.Add((n, k, m, j));
            }
        }
        return transfers;
    }

    static void ApplyTransfers(List<(Nucleus N, int GiveIndex, Nucleus M, int ReceiveIndex)> transfers)
    {
        foreach (var t in transfers)
        {
            var give = t.M.Ring[t.ReceiveIndex];
            t.N.Ring[t.GiveIndex] = new RingSlot(false, give.Color, give.Charge, true);
            t.M.Ring[t.ReceiveIndex] = new RingSlot(true, null, 0f, false);
        }
    }


    // ---- step 4: delivery to singularity / black hole / star (all 8
    // directions, unconditionally, for any charged -- non-hole -- slot) ----
    static void DeliveryStep(GameWorld w, List<Nucleus> nuclei)
    {
        foreach (var n in nuclei)
        {
            for (int k = 0; k < 8; k++)
            {
                var slot = n.Ring[k];
                if (slot == null || slot.IsHole) continue;

                int nr = n.Row + Adj8[k].DR;
                int nc = n.Col + Adj8[k].DC;
                if (!w.InBounds(nr, nc)) continue;

                bool isItem = IsItemColor(slot.Color);
                var cell = w.Grid[nr, nc];

                if (cell.IsSingularity)
                {
                    if (isItem)
                    {
                        string tier = slot.Color.Substring("nucleus:".Length);
                        w.CraftedNucleusInventory[tier] =
                            w.CraftedNucleusInventory.GetValueOrDefault(tier) + (int)slot.Charge;
                    }
                    else
                    {
                        SingularityReceive(w, slot.Color, slot.Charge);
                    }
                    slot.IsHole = true;
                    slot.Color = null;
                    slot.Charge = 0;
                }
                else if (cell.IsBlackhole)
                {
                    if (!isItem) w.Singularity.LostToBlackHoles += slot.Charge;
                    slot.IsHole = true;
                    slot.Color = null;
                    slot.Charge = 0;
                }
                else
                {
                    var starId = w.StarAt[nr, nc];
                    if (starId.HasValue && !isItem)
                    {
                        var star = w.Stars[starId.Value];
                        if (star.Recipe.ContainsKey(slot.Color))
                        {
                            star.Progress[slot.Color] = star.Progress.GetValueOrDefault(slot.Color) + slot.Charge;
                            slot.IsHole = true;
                            slot.Color = null;
                            slot.Charge = 0;
                            CraftAsManyAsPossible(star);
                        }
                        // A color outside the star's recipe just passes through untouched.
                    }
                }
            }
        }
    }

    static void CraftAsManyAsPossible(Star star)
    {
        while (star.Recipe.Count > 0 && star.Recipe.All(kv => star.Progress.GetValueOrDefault(kv.Key) >= kv.Value))
        {
            foreach (var kv in star.Recipe)
                star.Progress[kv.Key] -= kv.Value;
            star.ReadyCount += 1;
        }
    }

    // ---- singularity accumulation, color milestones, field expansion ----
    static void SingularityReceive(GameWorld w, string color, float amount)
    {
        w.Singularity.TotalReceivedEver += amount;
        w.Singularity.ExpansionPool += amount;

        if (!w.Singularity.ColorPools.TryGetValue(color, out var st))
        {
            st = new ColorPoolState();
            w.Singularity.ColorPools[color] = st;
        }
        st.Pool += amount;

        CheckColorMilestone(w, color, st);
        CheckExpansionMilestone(w);
    }

    static float ColorThresholdFor(GameWorld w, ColorPoolState st) =>
        w.Config.SingularitySpends
            ? (st.StarsAwarded == 0 ? w.Config.ColorUnlockCost : w.Config.ColorRepeatCost)
            : (w.Config.ColorUnlockCost + st.StarsAwarded * w.Config.ColorRepeatCost);

    static void CheckColorMilestone(GameWorld w, string color, ColorPoolState st)
    {
        while (st.Pool >= ColorThresholdFor(w, st))
        {
            float threshold = ColorThresholdFor(w, st);
            st.StarsAwarded++;
            w.ColorMilestoneCounts[color] = w.ColorMilestoneCounts.GetValueOrDefault(color) + 1;
            if (w.Config.SingularitySpends) st.Pool -= threshold;
        }
    }

    static float ExpansionThresholdSingle(GameWorld w, int n) =>
        (float)(w.Config.ExpansionBaseCost * Math.Pow(w.Config.ExpansionGrowth, n));

    static float ExpansionThresholdCumulative(GameWorld w, int n)
    {
        if (n <= 0) return 0f;
        if (Math.Abs(w.Config.ExpansionGrowth - 1) < 1e-9)
            return w.Config.ExpansionBaseCost * n;
        return (float)(w.Config.ExpansionBaseCost * (Math.Pow(w.Config.ExpansionGrowth, n) - 1) / (w.Config.ExpansionGrowth - 1));
    }

    static void CheckExpansionMilestone(GameWorld w)
    {
        while (true)
        {
            int n = w.Singularity.ExpansionsSoFar;
            float threshold = w.Config.SingularitySpends
                ? ExpansionThresholdSingle(w, n)
                : ExpansionThresholdCumulative(w, n + 1);
            if (w.Singularity.ExpansionPool < threshold) break;

            w.Singularity.ExpansionsSoFar++;
            if (w.Config.SingularitySpends) w.Singularity.ExpansionPool -= threshold;

            int newRows = Math.Min(w.Config.MaxFieldSize, w.Rows + w.Config.ExpansionStep);
            int newCols = Math.Min(w.Config.MaxFieldSize, w.Cols + w.Config.ExpansionStep);
            w.Resize(newRows, newCols);
        }
    }
}
