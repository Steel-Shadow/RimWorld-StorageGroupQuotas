using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace StorageGroupQuotas
{
    public enum QuotaMode
    {
        TotalCount,
        SimilarStacks
    }

    public sealed class StorageQuotaData : IExposable
    {
        private int defaultUpper;
        private QuotaMode quotaMode;
        private int similarStackCount = 2;
        private Dictionary<string, int> upperByDefName = new Dictionary<string, int>();
        private Dictionary<string, int> upperByCategoryDefName = new Dictionary<string, int>();

        public QuotaMode Mode
        {
            get => quotaMode;
            set => quotaMode = value;
        }

        public int SimilarStackCount
        {
            get => similarStackCount;
            set => similarStackCount = Math.Max(1, value);
        }

        public int DefaultUpper
        {
            get => defaultUpper;
            set => defaultUpper = Math.Max(0, value);
        }

        public bool Active => quotaMode == QuotaMode.SimilarStacks
            || defaultUpper > 0
            || upperByDefName.Count > 0
            || upperByCategoryDefName.Count > 0;

        public IEnumerable<string> OverriddenDefNames => upperByDefName.Keys;

        public IEnumerable<string> OverriddenCategoryDefNames => upperByCategoryDefName.Keys;

        public int EffectiveValue(ThingDef def)
        {
            if (def != null && upperByDefName.TryGetValue(def.defName, out int upper))
            {
                return upper;
            }

            return InheritedValue(def);
        }

        public int EffectiveValue(ThingCategoryDef category)
        {
            if (category != null
                && upperByCategoryDefName.TryGetValue(category.defName, out int upper))
            {
                return upper;
            }

            return InheritedValue(category);
        }

        public int InheritedValue(ThingDef def)
        {
            return TryFindClosestCategoryOverride(def, out int value, out _)
                ? value
                : defaultUpper;
        }

        public int InheritedValue(ThingCategoryDef category)
        {
            return TryFindCategoryOverride(category?.parent, out int value, out _)
                ? value
                : defaultUpper;
        }

        public ThingCategoryDef InheritedCategory(ThingDef def)
        {
            TryFindClosestCategoryOverride(def, out _, out ThingCategoryDef category);
            return category;
        }

        public ThingCategoryDef InheritedCategory(ThingCategoryDef category)
        {
            TryFindCategoryOverride(category?.parent, out _, out ThingCategoryDef parent);
            return parent;
        }

        public int EffectivePerStackUpper(ThingDef def)
        {
            if (quotaMode != QuotaMode.SimilarStacks || def == null)
            {
                return int.MaxValue;
            }

            int nativeLimit = Math.Max(1, def.stackLimit);
            int configured = EffectiveValue(def);
            return configured == 0 ? nativeLimit : Math.Min(configured, nativeLimit);
        }

        public int EffectiveTotalUpper(ThingDef def)
        {
            int configured = EffectiveValue(def);
            if (quotaMode == QuotaMode.TotalCount)
            {
                return configured == 0 ? int.MaxValue : configured;
            }

            long total = (long)EffectivePerStackUpper(def) * similarStackCount;
            return total >= int.MaxValue ? int.MaxValue : (int)total;
        }

        public bool HasOverride(ThingDef def)
        {
            return def != null && upperByDefName.ContainsKey(def.defName);
        }

        public int GetOverride(ThingDef def)
        {
            return def != null && upperByDefName.TryGetValue(def.defName, out int value) ? value : 0;
        }

        public void SetOverride(ThingDef def, int upper)
        {
            if (def == null)
            {
                return;
            }

            upperByDefName[def.defName] = Math.Max(0, upper);
        }

        public void RemoveOverride(ThingDef def)
        {
            if (def != null)
            {
                upperByDefName.Remove(def.defName);
            }
        }

        public bool HasOverride(ThingCategoryDef category)
        {
            return category != null && upperByCategoryDefName.ContainsKey(category.defName);
        }

        public int GetOverride(ThingCategoryDef category)
        {
            return category != null
                && upperByCategoryDefName.TryGetValue(category.defName, out int value)
                ? value
                : 0;
        }

        public void SetOverride(ThingCategoryDef category, int upper)
        {
            if (category == null)
            {
                return;
            }

            upperByCategoryDefName[category.defName] = Math.Max(0, upper);
        }

        public void RemoveOverride(ThingCategoryDef category)
        {
            if (category != null)
            {
                upperByCategoryDefName.Remove(category.defName);
            }
        }

        public StorageQuotaData Clone()
        {
            return new StorageQuotaData
            {
                defaultUpper = defaultUpper,
                quotaMode = quotaMode,
                similarStackCount = similarStackCount,
                upperByDefName = new Dictionary<string, int>(upperByDefName),
                upperByCategoryDefName = new Dictionary<string, int>(upperByCategoryDefName)
            };
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref defaultUpper, "defaultUpper", 0);
            Scribe_Values.Look(ref quotaMode, "quotaMode", QuotaMode.TotalCount);
            Scribe_Values.Look(ref similarStackCount, "similarStackCount", 2);
            Scribe_Collections.Look(ref upperByDefName, "upperByDefName", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(
                ref upperByCategoryDefName,
                "upperByCategoryDefName",
                LookMode.Value,
                LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (upperByDefName == null)
                {
                    upperByDefName = new Dictionary<string, int>();
                }

                if (upperByCategoryDefName == null)
                {
                    upperByCategoryDefName = new Dictionary<string, int>();
                }

                similarStackCount = Math.Max(1, similarStackCount);
            }
        }

        private bool TryFindClosestCategoryOverride(
            ThingDef def,
            out int value,
            out ThingCategoryDef source)
        {
            return TryFindCategoryOverride(def?.FirstThingCategory, out value, out source);
        }

        private bool TryFindCategoryOverride(
            ThingCategoryDef category,
            out int value,
            out ThingCategoryDef source)
        {
            for (int distance = 0; category != null && distance < 128; distance++)
            {
                if (upperByCategoryDefName.TryGetValue(category.defName, out value))
                {
                    source = category;
                    return true;
                }

                category = category.parent;
            }

            value = 0;
            source = null;
            return false;
        }
    }
}
