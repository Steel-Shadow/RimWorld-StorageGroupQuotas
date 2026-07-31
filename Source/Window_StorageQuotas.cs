using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace StorageGroupQuotas
{
    public sealed class Window_StorageQuotas : Window
    {
        private const int MaxQuota = 1000000000;
        private readonly StorageSettings settings;
        private readonly StorageQuotaData data;
        private readonly List<ThingDef> defs;
        private readonly Dictionary<string, string> numberBuffers = new Dictionary<string, string>();
        private Vector2 scrollPosition;
        private string search = string.Empty;
        private string defaultBuffer;
        private string similarStackCountBuffer;

        public override Vector2 InitialSize => new Vector2(780f, 680f);

        public Window_StorageQuotas(StorageSettings settings)
        {
            this.settings = settings;
            data = QuotaDataStore.Get(settings);
            defaultBuffer = data.DefaultUpper.ToString();
            similarStackCountBuffer = data.SimilarStackCount.ToString();
            doCloseX = true;
            draggable = true;
            absorbInputAroundWindow = false;

            HashSet<ThingDef> candidates = new HashSet<ThingDef>(
                DefDatabase<ThingDef>.AllDefsListForReading.Where(def =>
                    def.EverStorable(false) && settings.filter.Allows(def)));

            foreach (string defName in data.OverriddenDefNames.ToList())
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (def != null)
                {
                    candidates.Add(def);
                }
            }

            defs = candidates.OrderBy(def => def.LabelCap.ToString()).ToList();
        }

        public override void DoWindowContents(Rect inRect)
        {
            ISlotGroup scope = QuotaUtility.ScopeForSettings(settings);
            string scopeLabel = scope == null
                ? settings.owner?.ToString() ?? "Storage"
                : SlotGroup.GetGroupLabel(scope);
            Dictionary<ThingDef, int> counts = scope?.HeldThings
                .GroupBy(thing => thing.def)
                .ToDictionary(group => group.Key, group => group.Sum(thing => thing.stackCount))
                ?? new Dictionary<ThingDef, int>();
            Dictionary<ThingDef, int> stackCounts = scope?.HeldThings
                .GroupBy(thing => thing.def)
                .ToDictionary(group => group.Key, group => group.Count())
                ?? new Dictionary<ThingDef, int>();

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), "SGQ_WindowTitle".Translate(scopeLabel));
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, 40f, 90f, 28f), "SGQ_Mode".Translate());
            if (Widgets.ButtonText(
                new Rect(94f, 36f, 190f, 30f),
                (data.Mode == QuotaMode.TotalCount ? "✓ " : string.Empty) + "SGQ_ModeTotal".Translate()))
            {
                SetMode(QuotaMode.TotalCount);
            }

            if (Widgets.ButtonText(
                new Rect(292f, 36f, 220f, 30f),
                (data.Mode == QuotaMode.SimilarStacks ? "✓ " : string.Empty) + "SGQ_ModeSimilar".Translate()))
            {
                SetMode(QuotaMode.SimilarStacks);
            }

            string description = data.Mode == QuotaMode.TotalCount
                ? "SGQ_DescriptionTotal".Translate()
                : "SGQ_DescriptionSimilar".Translate(data.SimilarStackCount);
            Widgets.Label(new Rect(0f, 74f, inRect.width, 46f), description);

            int oldDefault = data.DefaultUpper;
            string defaultLabel = data.Mode == QuotaMode.TotalCount
                ? "SGQ_DefaultTotal".Translate()
                : "SGQ_DefaultPerStack".Translate();
            Widgets.Label(new Rect(0f, 126f, 190f, 28f), defaultLabel);
            int defaultUpper = data.DefaultUpper;
            Widgets.TextFieldNumeric(
                new Rect(195f, 122f, 105f, 28f),
                ref defaultUpper,
                ref defaultBuffer,
                0,
                MaxQuota);
            data.DefaultUpper = defaultUpper;
            if (data.Mode == QuotaMode.SimilarStacks)
            {
                Widgets.Label(new Rect(322f, 126f, 112f, 28f), "SGQ_SimilarCount".Translate());
                int similarCount = data.SimilarStackCount;
                Widgets.TextFieldNumeric(
                    new Rect(438f, 122f, 80f, 28f),
                    ref similarCount,
                    ref similarStackCountBuffer,
                    1,
                    1000);
                if (similarCount != data.SimilarStackCount)
                {
                    data.SimilarStackCount = similarCount;
                    QuotaUtility.NotifySettingsChanged(settings);
                }
            }

            if (oldDefault != data.DefaultUpper)
            {
                QuotaUtility.NotifySettingsChanged(settings);
            }

            string zeroHelp = data.Mode == QuotaMode.TotalCount
                ? "SGQ_ZeroHelpTotal".Translate()
                : "SGQ_ZeroHelpSimilar".Translate();
            Widgets.Label(new Rect(0f, 154f, inRect.width, 25f), zeroHelp);

            search = Widgets.TextField(new Rect(0f, 183f, inRect.width, 30f), search);

            float tableTop = 221f;
            Widgets.DrawMenuSection(new Rect(0f, tableTop, inRect.width, inRect.height - tableTop - 38f));
            Widgets.Label(new Rect(10f, tableTop + 6f, 300f, 25f), "SGQ_Item".Translate());
            Widgets.Label(new Rect(318f, tableTop + 6f, 115f, 25f), "SGQ_CurrentAndStacks".Translate());
            string quotaHeader = data.Mode == QuotaMode.TotalCount
                ? "SGQ_TotalQuota".Translate()
                : "SGQ_PerStackQuota".Translate();
            Widgets.Label(new Rect(440f, tableTop + 6f, 150f, 25f), quotaHeader);

            List<ThingDef> visible = defs
                .Where(def => search.NullOrEmpty()
                    || def.LabelCap.ToString().IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                    || def.defName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            Rect outRect = new Rect(6f, tableTop + 34f, inRect.width - 12f, inRect.height - tableTop - 78f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, visible.Count * 34f);
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
            for (int i = 0; i < visible.Count; i++)
            {
                ThingDef def = visible[i];
                Rect row = new Rect(0f, i * 34f, viewRect.width, 32f);
                if (i % 2 == 1)
                {
                    Widgets.DrawLightHighlight(row);
                }

                Rect iconRect = new Rect(6f, row.y + 3f, 26f, 26f);
                Widgets.DefIcon(iconRect, def);
                Widgets.Label(new Rect(38f, row.y + 4f, 268f, 25f), def.LabelCap);
                counts.TryGetValue(def, out int current);
                stackCounts.TryGetValue(def, out int currentStacks);
                Widgets.Label(new Rect(312f, row.y + 4f, 112f, 25f),
                    "SGQ_CountAndStacks".Translate(current, currentStacks));

                bool hasOverride = data.HasOverride(def);
                if (!hasOverride)
                {
                    string inherited = data.DefaultUpper == 0
                        ? (data.Mode == QuotaMode.TotalCount
                            ? "SGQ_Unlimited".Translate()
                            : "SGQ_NativeStackLimit".Translate())
                        : data.DefaultUpper.ToString();
                    if (Widgets.ButtonText(new Rect(432f, row.y + 2f, 160f, 28f),
                        "SGQ_Inherit".Translate() + ": " + inherited))
                    {
                        int initial = data.EffectiveValue(def) == 0
                            ? Math.Max(1, def.stackLimit)
                            : data.EffectiveValue(def);
                        data.SetOverride(def, initial);
                        numberBuffers[def.defName] = initial.ToString();
                        QuotaUtility.NotifySettingsChanged(settings);
                    }
                }
                else
                {
                    int value = data.GetOverride(def);
                    if (!numberBuffers.TryGetValue(def.defName, out string buffer))
                    {
                        buffer = value.ToString();
                    }

                    int old = value;
                    Widgets.TextFieldNumeric(
                        new Rect(432f, row.y + 2f, 95f, 28f),
                        ref value,
                        ref buffer,
                        0,
                        MaxQuota);
                    numberBuffers[def.defName] = buffer;
                    if (value != old)
                    {
                        data.SetOverride(def, value);
                        QuotaUtility.NotifySettingsChanged(settings);
                    }

                    if (Widgets.ButtonText(new Rect(535f, row.y + 2f, 150f, 28f), "SGQ_RemoveOverride".Translate()))
                    {
                        data.RemoveOverride(def);
                        numberBuffers.Remove(def.defName);
                        QuotaUtility.NotifySettingsChanged(settings);
                    }
                }
            }

            Widgets.EndScrollView();

            int overflow = 0;
            int rebalanceStacks = 0;
            if (scope != null)
            {
                foreach (ThingDef def in defs)
                {
                    int upper = data.EffectiveTotalUpper(def);
                    if (upper != int.MaxValue)
                    {
                        counts.TryGetValue(def, out int current);
                        overflow += Math.Max(0, current - upper);
                    }

                    if (data.Mode == QuotaMode.SimilarStacks)
                    {
                        int perStack = data.EffectivePerStackUpper(def);
                        rebalanceStacks += scope.HeldThings.Count(thing =>
                            thing.def == def && thing.stackCount > perStack);
                        stackCounts.TryGetValue(def, out int currentStacks);
                        rebalanceStacks += Math.Max(0, currentStacks - data.SimilarStackCount);
                    }
                }
            }

            string status;
            if (overflow > 0)
            {
                status = "SGQ_OverflowStatus".Translate(overflow);
            }
            else if (rebalanceStacks > 0)
            {
                status = "SGQ_RebalanceStatus".Translate(rebalanceStacks);
            }
            else
            {
                status = "SGQ_NoOverflow".Translate();
            }

            Widgets.Label(new Rect(4f, inRect.height - 32f, inRect.width - 8f, 28f), status);
        }

        private void SetMode(QuotaMode mode)
        {
            if (data.Mode == mode)
            {
                return;
            }

            data.Mode = mode;
            QuotaUtility.NotifySettingsChanged(settings);
        }
    }
}
