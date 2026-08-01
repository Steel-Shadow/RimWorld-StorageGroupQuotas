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
        private const int MaxStackCount = 1000;
        private const int StorageTreeOpenMask = 8;
        private const float EditorStartX = 384f;
        private const float SimilarEditorWidth = 148f;
        private const float SimilarEditorGap = 8f;
        private const float TotalEditorWidth = 304f;

        private enum QuotaField
        {
            Upper,
            MaxStacks
        }

        private readonly StorageSettings settings;
        private readonly StorageQuotaData data;
        private readonly List<ThingDef> defs = new List<ThingDef>();
        private readonly HashSet<ThingDef> listedDefs = new HashSet<ThingDef>();
        private readonly HashSet<ThingDef> candidateDefs = new HashSet<ThingDef>();
        private readonly HashSet<ThingCategoryDef> listedOverrideCategories =
            new HashSet<ThingCategoryDef>();
        private readonly HashSet<ThingCategoryDef> candidateOverrideCategories =
            new HashSet<ThingCategoryDef>();
        private readonly Dictionary<string, string> numberBuffers = new Dictionary<string, string>();
        private readonly HashSet<ThingCategoryDef> expandedCategories = new HashSet<ThingCategoryDef>();
        private readonly List<QuotaTreeModel.Row> visibleRows = new List<QuotaTreeModel.Row>();
        private QuotaTreeModel treeModel;
        private Vector2 scrollPosition;
        private string search = string.Empty;
        private string defaultBuffer;
        private string defaultMaxStacksBuffer;
        private int lastTreeRefreshFrame = -1;

        public override Vector2 InitialSize => new Vector2(780f, 680f);

        public Window_StorageQuotas(StorageSettings settings)
        {
            this.settings = settings;
            data = QuotaDataStore.Get(settings);
            defaultBuffer = data.DefaultUpper.ToString();
            defaultMaxStacksBuffer = data.DefaultMaxStacks.ToString();
            doCloseX = true;
            draggable = true;
            absorbInputAroundWindow = false;
            RefreshTree(QuotaUtility.ScopeForSettings(settings));
        }

        public override void DoWindowContents(Rect inRect)
        {
            ISlotGroup scope = QuotaUtility.ScopeForSettings(settings);
            RefreshTree(scope);
            SynchronizeExpandedCategories();
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
                : "SGQ_DescriptionSimilar".Translate();
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
                Rect defaultMaxStacksLabelRect = new Rect(322f, 126f, 132f, 28f);
                string defaultMaxStacksLabel = "SGQ_DefaultMaxStacks".Translate();
                Widgets.LabelEllipses(defaultMaxStacksLabelRect, defaultMaxStacksLabel);
                TooltipHandler.TipRegion(defaultMaxStacksLabelRect, defaultMaxStacksLabel);
                int similarCount = data.DefaultMaxStacks;
                Widgets.TextFieldNumeric(
                    new Rect(458f, 122f, 80f, 28f),
                    ref similarCount,
                    ref defaultMaxStacksBuffer,
                    1,
                    MaxStackCount);
                if (similarCount != data.DefaultMaxStacks)
                {
                    data.DefaultMaxStacks = similarCount;
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

            string oldSearch = search;
            search = Widgets.TextField(new Rect(0f, 183f, inRect.width, 30f), search);
            if (!string.Equals(oldSearch, search, StringComparison.Ordinal))
            {
                scrollPosition.y = 0f;
            }

            float tableTop = 221f;
            Widgets.DrawMenuSection(new Rect(0f, tableTop, inRect.width, inRect.height - tableTop - 38f));
            Widgets.Label(new Rect(10f, tableTop + 6f, 272f, 25f), "SGQ_ItemOrCategory".Translate());
            Rect currentHeaderRect = new Rect(290f, tableTop + 6f, 92f, 25f);
            string currentHeader = "SGQ_CurrentAndStacks".Translate();
            Widgets.LabelEllipses(currentHeaderRect, currentHeader);
            TooltipHandler.TipRegion(currentHeaderRect, currentHeader);
            string quotaHeader = data.Mode == QuotaMode.TotalCount
                ? "SGQ_TotalQuota".Translate()
                : "SGQ_PerStackQuota".Translate();
            Rect quotaHeaderRect = new Rect(
                390f,
                tableTop + 6f,
                data.Mode == QuotaMode.TotalCount ? TotalEditorWidth : SimilarEditorWidth,
                25f);
            Widgets.Label(quotaHeaderRect, quotaHeader);
            TooltipHandler.TipRegion(
                quotaHeaderRect,
                (data.Mode == QuotaMode.TotalCount
                    ? "SGQ_TotalQuotaTooltip"
                    : "SGQ_PerStackQuotaTooltip").Translate());
            if (data.Mode == QuotaMode.SimilarStacks)
            {
                Rect maxStacksHeaderRect = new Rect(
                    546f,
                    tableTop + 6f,
                    SimilarEditorWidth,
                    25f);
                Widgets.Label(maxStacksHeaderRect, "SGQ_MaxStacks".Translate());
                TooltipHandler.TipRegion(
                    maxStacksHeaderRect,
                    "SGQ_MaxStacksTooltip".Translate());
            }

            treeModel.BuildRows(search, expandedCategories, visibleRows);

            Rect outRect = new Rect(6f, tableTop + 34f, inRect.width - 12f, inRect.height - tableTop - 78f);
            Rect viewRect = new Rect(
                0f,
                0f,
                outRect.width - 16f,
                Math.Max(outRect.height, visibleRows.Count * 34f));
            scrollPosition.y = Mathf.Clamp(
                scrollPosition.y,
                0f,
                Math.Max(0f, viewRect.height - outRect.height));
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
            for (int i = 0; i < visibleRows.Count; i++)
            {
                QuotaTreeModel.Row treeRow = visibleRows[i];
                Rect row = new Rect(0f, i * 34f, viewRect.width, 32f);
                if (row.yMax < scrollPosition.y || row.y > scrollPosition.y + outRect.height)
                {
                    continue;
                }

                if (i % 2 == 1)
                {
                    Widgets.DrawLightHighlight(row);
                }

                Widgets.DrawHighlightIfMouseover(row);
                float indent = Math.Min(170f, treeRow.Depth * 18f);
                if (treeRow.IsCategory)
                {
                    ThingCategoryDef category = treeRow.Category;
                    float labelX = 8f + indent;
                    if (treeRow.HasChildren)
                    {
                        bool searchExpanded = !string.IsNullOrWhiteSpace(search);
                        bool expanded = searchExpanded || expandedCategories.Contains(category);
                        Rect toggleRect = new Rect(labelX, row.y + 7f, 18f, 18f);
                        bool toggleClicked = searchExpanded
                            ? false
                            : Widgets.ButtonImage(
                                toggleRect,
                                expanded ? TexButton.Collapse : TexButton.Reveal);
                        if (searchExpanded)
                        {
                            Color oldColor = GUI.color;
                            GUI.color = Color.gray;
                            GUI.DrawTexture(toggleRect, TexButton.Collapse);
                            GUI.color = oldColor;
                        }
                        else if (toggleClicked)
                        {
                            SetCategoryExpanded(category, !expanded);
                        }

                        if (!searchExpanded)
                        {
                            TooltipHandler.TipRegion(
                                toggleRect,
                                (expanded ? "SGQ_CollapseCategory" : "SGQ_ExpandCategory")
                                    .Translate(category.LabelCap));
                        }

                        labelX += 22f;
                    }

                    string categoryLabel = category.LabelCap.ToString();
                    Rect categoryLabelRect = new Rect(
                        labelX,
                        row.y + 4f,
                        Math.Max(10f, 276f - labelX),
                        25f);
                    Widgets.LabelEllipses(categoryLabelRect, categoryLabel);
                    Widgets.Label(new Rect(284f, row.y + 4f, 92f, 25f), "—");
                    TooltipHandler.TipRegion(
                        categoryLabelRect,
                        categoryLabel
                            + "\n\n"
                            + (data.Mode == QuotaMode.TotalCount
                                ? "SGQ_CategoryTotalTooltip"
                                : "SGQ_CategorySimilarTooltip").Translate());
                    DrawQuotaEditor(row, null, category);
                }
                else
                {
                    ThingDef def = treeRow.Thing;
                    bool allowedByFilter = settings.filter.Allows(def);
                    float iconX = 8f + indent;
                    Rect iconRect = new Rect(iconX, row.y + 3f, 26f, 26f);
                    Color oldColor = GUI.color;
                    if (!allowedByFilter)
                    {
                        GUI.color = Color.gray;
                    }

                    Widgets.DefIcon(iconRect, def);
                    string thingLabel = def.LabelCap.ToString();
                    Rect thingLabelRect = new Rect(
                        iconX + 32f,
                        row.y + 4f,
                        Math.Max(10f, 276f - iconX - 32f),
                        25f);
                    Widgets.LabelEllipses(thingLabelRect, thingLabel);
                    GUI.color = oldColor;
                    TooltipHandler.TipRegion(
                        thingLabelRect,
                        allowedByFilter
                            ? thingLabel
                            : thingLabel + "\n\n" + "SGQ_RetainedDisallowedItem".Translate());
                    counts.TryGetValue(def, out int current);
                    stackCounts.TryGetValue(def, out int currentStacks);
                    Rect currentRect = new Rect(284f, row.y + 4f, 92f, 25f);
                    string currentLabel = "SGQ_CountAndStacks".Translate(current, currentStacks);
                    Widgets.LabelEllipses(currentRect, currentLabel);
                    TooltipHandler.TipRegion(currentRect, currentLabel);
                    DrawQuotaEditor(row, def, null);
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
                        rebalanceStacks += Math.Max(0, currentStacks - data.EffectiveMaxStacks(def));
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

        private void ExpandCategoryPath(ThingCategoryDef category)
        {
            for (int depth = 0;
                category != null && category != ThingCategoryDefOf.Root && depth < 128;
                depth++)
            {
                SetCategoryExpanded(category, true);
                category = category.parent;
            }
        }

        private void RefreshTree(ISlotGroup scope)
        {
            if (treeModel != null && lastTreeRefreshFrame == Time.frameCount)
            {
                return;
            }

            lastTreeRefreshFrame = Time.frameCount;
            candidateDefs.Clear();
            foreach (ThingDef def in settings.filter.AllowedThingDefs)
            {
                if (def != null && def.EverStorable(false))
                {
                    candidateDefs.Add(def);
                }
            }

            foreach (string defName in data.OverriddenDefNames)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (def != null)
                {
                    candidateDefs.Add(def);
                }
            }

            if (scope != null)
            {
                foreach (Thing thing in scope.HeldThings)
                {
                    if (thing?.def != null)
                    {
                        candidateDefs.Add(thing.def);
                    }
                }
            }

            candidateOverrideCategories.Clear();
            foreach (string defName in data.OverriddenCategoryDefNames)
            {
                ThingCategoryDef category = DefDatabase<ThingCategoryDef>.GetNamedSilentFail(defName);
                if (category != null)
                {
                    candidateOverrideCategories.Add(category);
                }
            }

            if (treeModel != null
                && listedDefs.SetEquals(candidateDefs)
                && listedOverrideCategories.SetEquals(candidateOverrideCategories))
            {
                return;
            }

            listedDefs.Clear();
            listedDefs.UnionWith(candidateDefs);
            listedOverrideCategories.Clear();
            listedOverrideCategories.UnionWith(candidateOverrideCategories);

            defs.Clear();
            defs.AddRange(candidateDefs.OrderBy(def => def.LabelCap.ToString()));
            treeModel = new QuotaTreeModel(defs, listedOverrideCategories);
            SynchronizeExpandedCategories();
        }

        private void SynchronizeExpandedCategories()
        {
            if (treeModel == null)
            {
                expandedCategories.Clear();
                return;
            }

            expandedCategories.RemoveWhere(category => !treeModel.ContainsCategory(category));
            foreach (ThingCategoryDef category in treeModel.Categories)
            {
                if (category?.treeNode == null)
                {
                    continue;
                }

                if (category.treeNode.IsOpen(StorageTreeOpenMask))
                {
                    expandedCategories.Add(category);
                }
                else
                {
                    expandedCategories.Remove(category);
                }
            }
        }

        private void SetCategoryExpanded(ThingCategoryDef category, bool expanded)
        {
            if (category == null)
            {
                return;
            }

            category.treeNode?.SetOpen(StorageTreeOpenMask, expanded);
            if (expanded)
            {
                expandedCategories.Add(category);
            }
            else
            {
                expandedCategories.Remove(category);
            }
        }

        private void DrawQuotaEditor(
            Rect row,
            ThingDef thing,
            ThingCategoryDef category)
        {
            if (data.Mode == QuotaMode.TotalCount)
            {
                DrawQuotaFieldEditor(
                    new Rect(EditorStartX, row.y + 2f, TotalEditorWidth, 28f),
                    thing,
                    category,
                    QuotaField.Upper);
                return;
            }

            DrawQuotaFieldEditor(
                new Rect(EditorStartX, row.y + 2f, SimilarEditorWidth, 28f),
                thing,
                category,
                QuotaField.Upper);
            DrawQuotaFieldEditor(
                new Rect(
                    EditorStartX + SimilarEditorWidth + SimilarEditorGap,
                    row.y + 2f,
                    SimilarEditorWidth,
                    28f),
                thing,
                category,
                QuotaField.MaxStacks);
        }

        private void DrawQuotaFieldEditor(
            Rect editorRect,
            ThingDef thing,
            ThingCategoryDef category,
            QuotaField field)
        {
            bool isThing = thing != null;
            bool isMaxStacks = field == QuotaField.MaxStacks;
            bool hasOverride = isMaxStacks
                ? (isThing
                    ? data.HasMaxStacksOverride(thing)
                    : data.HasMaxStacksOverride(category))
                : (isThing
                    ? data.HasOverride(thing)
                    : data.HasOverride(category));
            string bufferKey = (isMaxStacks ? "stacks:" : "upper:")
                + (isThing ? "thing:" : "category:")
                + (isThing ? thing.defName : category.defName);

            if (!hasOverride)
            {
                int inheritedValue = isMaxStacks
                    ? (isThing
                        ? data.InheritedMaxStacks(thing)
                        : data.InheritedMaxStacks(category))
                    : (isThing
                        ? data.InheritedValue(thing)
                        : data.InheritedValue(category));
                ThingCategoryDef inheritedCategory = isMaxStacks
                    ? (isThing
                        ? data.InheritedMaxStacksCategory(thing)
                        : data.InheritedMaxStacksCategory(category))
                    : (isThing
                        ? data.InheritedCategory(thing)
                        : data.InheritedCategory(category));
                string inheritedSource = inheritedCategory?.LabelCap.ToString()
                    ?? "SGQ_GlobalDefault".Translate();
                if (Widgets.ButtonText(
                    editorRect,
                    "SGQ_InheritCompact".Translate(FormatQuotaValue(inheritedValue, field, true))))
                {
                    int initial = inheritedValue;

                    if (isMaxStacks)
                    {
                        if (isThing)
                        {
                            data.SetMaxStacksOverride(thing, initial);
                        }
                        else
                        {
                            data.SetMaxStacksOverride(category, initial);
                        }
                    }
                    else if (isThing)
                    {
                        data.SetOverride(thing, initial);
                    }
                    else
                    {
                        data.SetOverride(category, initial);
                    }

                    numberBuffers[bufferKey] = initial.ToString();
                    ExpandCategoryPath(isThing ? thing.FirstThingCategory : category);
                    QuotaUtility.NotifySettingsChanged(settings);
                }

                TooltipHandler.TipRegion(
                    editorRect,
                    "SGQ_InheritedFieldTooltip".Translate(
                        FormatQuotaValue(inheritedValue, field, false),
                        inheritedSource));
                return;
            }

            int value = isMaxStacks
                ? (isThing
                    ? data.GetMaxStacksOverride(thing)
                    : data.GetMaxStacksOverride(category))
                : (isThing ? data.GetOverride(thing) : data.GetOverride(category));
            if (!numberBuffers.TryGetValue(bufferKey, out string buffer))
            {
                buffer = value.ToString();
            }

            int oldValue = value;
            float removeWidth = data.Mode == QuotaMode.SimilarStacks ? 28f : 150f;
            float gap = 8f;
            Rect valueRect = new Rect(
                editorRect.x,
                editorRect.y,
                editorRect.width - removeWidth - gap,
                editorRect.height);
            Rect removeRect = new Rect(
                valueRect.xMax + gap,
                editorRect.y,
                removeWidth,
                editorRect.height);
            Widgets.TextFieldNumeric(
                valueRect,
                ref value,
                ref buffer,
                isMaxStacks ? 1 : 0,
                isMaxStacks ? MaxStackCount : MaxQuota);
            numberBuffers[bufferKey] = buffer;
            if (value != oldValue)
            {
                if (isMaxStacks)
                {
                    if (isThing)
                    {
                        data.SetMaxStacksOverride(thing, value);
                    }
                    else
                    {
                        data.SetMaxStacksOverride(category, value);
                    }
                }
                else if (isThing)
                {
                    data.SetOverride(thing, value);
                }
                else
                {
                    data.SetOverride(category, value);
                }

                QuotaUtility.NotifySettingsChanged(settings);
            }

            string removeLabel = data.Mode == QuotaMode.SimilarStacks
                ? "×"
                : "SGQ_RemoveOverride".Translate();
            if (Widgets.ButtonText(removeRect, removeLabel))
            {
                if (isMaxStacks)
                {
                    if (isThing)
                    {
                        data.RemoveMaxStacksOverride(thing);
                    }
                    else
                    {
                        data.RemoveMaxStacksOverride(category);
                    }
                }
                else if (isThing)
                {
                    data.RemoveOverride(thing);
                }
                else
                {
                    data.RemoveOverride(category);
                }

                numberBuffers.Remove(bufferKey);
                QuotaUtility.NotifySettingsChanged(settings);
            }

            int inheritedAfterRemoval = isMaxStacks
                ? (isThing
                    ? data.InheritedMaxStacks(thing)
                    : data.InheritedMaxStacks(category))
                : (isThing
                    ? data.InheritedValue(thing)
                    : data.InheritedValue(category));
            ThingCategoryDef inheritedCategoryAfterRemoval = isMaxStacks
                ? (isThing
                    ? data.InheritedMaxStacksCategory(thing)
                    : data.InheritedMaxStacksCategory(category))
                : (isThing
                    ? data.InheritedCategory(thing)
                    : data.InheritedCategory(category));
            string inheritedSourceAfterRemoval = inheritedCategoryAfterRemoval?.LabelCap.ToString()
                ?? "SGQ_GlobalDefault".Translate();
            string fieldLabel = (isMaxStacks
                ? "SGQ_MaxStacks"
                : data.Mode == QuotaMode.TotalCount
                    ? "SGQ_TotalQuota"
                    : "SGQ_PerStackQuota").Translate();
            TooltipHandler.TipRegion(
                removeRect,
                "SGQ_RemoveFieldOverrideTooltip".Translate(
                    fieldLabel,
                    FormatQuotaValue(inheritedAfterRemoval, field, false),
                    inheritedSourceAfterRemoval));
        }

        private string FormatQuotaValue(int value, QuotaField field, bool compact)
        {
            if (field == QuotaField.MaxStacks || value > 0)
            {
                return value.ToString();
            }

            return data.Mode == QuotaMode.TotalCount
                ? "SGQ_Unlimited".Translate()
                : (compact
                    ? "SGQ_NativeStackLimitCompact".Translate()
                    : "SGQ_NativeStackLimit".Translate());
        }
    }
}
