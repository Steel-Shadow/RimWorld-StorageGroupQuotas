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
        private int similarStackCount = 1;
        private Dictionary<string, int> upperByDefName = new Dictionary<string, int>();
        private Dictionary<string, int> upperByCategoryDefName = new Dictionary<string, int>();
        private Dictionary<string, int> maxStacksByDefName = new Dictionary<string, int>();
        private Dictionary<string, int> maxStacksByCategoryDefName = new Dictionary<string, int>();

        public QuotaMode Mode
        {
            get => quotaMode;
            set => quotaMode = value;
        }

        // Retained as the legacy public name for the global default N.
        public int SimilarStackCount
        {
            get => DefaultMaxStacks;
            set => DefaultMaxStacks = value;
        }

        public int DefaultMaxStacks
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

        public bool HasPersistentSettings => Active
            || similarStackCount != 1
            || maxStacksByDefName.Count > 0
            || maxStacksByCategoryDefName.Count > 0;

        public IEnumerable<string> OverriddenDefNames
        {
            get
            {
                foreach (string defName in upperByDefName.Keys)
                {
                    yield return defName;
                }

                foreach (string defName in maxStacksByDefName.Keys)
                {
                    if (!upperByDefName.ContainsKey(defName))
                    {
                        yield return defName;
                    }
                }
            }
        }

        public IEnumerable<string> OverriddenCategoryDefNames
        {
            get
            {
                foreach (string defName in upperByCategoryDefName.Keys)
                {
                    yield return defName;
                }

                foreach (string defName in maxStacksByCategoryDefName.Keys)
                {
                    if (!upperByCategoryDefName.ContainsKey(defName))
                    {
                        yield return defName;
                    }
                }
            }
        }

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

            long total = (long)EffectivePerStackUpper(def) * EffectiveMaxStacks(def);
            return total >= int.MaxValue ? int.MaxValue : (int)total;
        }

        public int EffectiveMaxStacks(ThingDef def)
        {
            if (def != null && maxStacksByDefName.TryGetValue(def.defName, out int count))
            {
                return Math.Max(1, count);
            }

            return InheritedMaxStacks(def);
        }

        public int EffectiveMaxStacks(ThingCategoryDef category)
        {
            if (category != null
                && maxStacksByCategoryDefName.TryGetValue(category.defName, out int count))
            {
                return Math.Max(1, count);
            }

            return InheritedMaxStacks(category);
        }

        public int InheritedMaxStacks(ThingDef def)
        {
            return TryFindClosestCategoryMaxStacksOverride(def, out int value, out _)
                ? value
                : Math.Max(1, similarStackCount);
        }

        public int InheritedMaxStacks(ThingCategoryDef category)
        {
            return TryFindCategoryMaxStacksOverride(category?.parent, out int value, out _)
                ? value
                : Math.Max(1, similarStackCount);
        }

        public ThingCategoryDef InheritedMaxStacksCategory(ThingDef def)
        {
            TryFindClosestCategoryMaxStacksOverride(def, out _, out ThingCategoryDef category);
            return category;
        }

        public ThingCategoryDef InheritedMaxStacksCategory(ThingCategoryDef category)
        {
            TryFindCategoryMaxStacksOverride(category?.parent, out _, out ThingCategoryDef parent);
            return parent;
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

        public bool HasMaxStacksOverride(ThingDef def)
        {
            return def != null && maxStacksByDefName.ContainsKey(def.defName);
        }

        public int GetMaxStacksOverride(ThingDef def)
        {
            return def != null
                && maxStacksByDefName.TryGetValue(def.defName, out int value)
                ? Math.Max(1, value)
                : 1;
        }

        public void SetMaxStacksOverride(ThingDef def, int count)
        {
            if (def != null)
            {
                maxStacksByDefName[def.defName] = Math.Max(1, count);
            }
        }

        public void RemoveMaxStacksOverride(ThingDef def)
        {
            if (def != null)
            {
                maxStacksByDefName.Remove(def.defName);
            }
        }

        public bool HasMaxStacksOverride(ThingCategoryDef category)
        {
            return category != null && maxStacksByCategoryDefName.ContainsKey(category.defName);
        }

        public int GetMaxStacksOverride(ThingCategoryDef category)
        {
            return category != null
                && maxStacksByCategoryDefName.TryGetValue(category.defName, out int value)
                ? Math.Max(1, value)
                : 1;
        }

        public void SetMaxStacksOverride(ThingCategoryDef category, int count)
        {
            if (category != null)
            {
                maxStacksByCategoryDefName[category.defName] = Math.Max(1, count);
            }
        }

        public void RemoveMaxStacksOverride(ThingCategoryDef category)
        {
            if (category != null)
            {
                maxStacksByCategoryDefName.Remove(category.defName);
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
                upperByCategoryDefName = new Dictionary<string, int>(upperByCategoryDefName),
                maxStacksByDefName = new Dictionary<string, int>(maxStacksByDefName),
                maxStacksByCategoryDefName = new Dictionary<string, int>(maxStacksByCategoryDefName)
            };
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref defaultUpper, "defaultUpper", 0);
            Scribe_Values.Look(ref quotaMode, "quotaMode", QuotaMode.TotalCount);
            // Keep 2 as the legacy Scribe default: older saves commonly omitted the field
            // when they used the old default. New instances start at 1 and therefore save it explicitly.
            Scribe_Values.Look(ref similarStackCount, "similarStackCount", 2);
            Scribe_Collections.Look(ref upperByDefName, "upperByDefName", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(
                ref upperByCategoryDefName,
                "upperByCategoryDefName",
                LookMode.Value,
                LookMode.Value);
            Scribe_Collections.Look(
                ref maxStacksByDefName,
                "maxStacksByDefName",
                LookMode.Value,
                LookMode.Value);
            Scribe_Collections.Look(
                ref maxStacksByCategoryDefName,
                "maxStacksByCategoryDefName",
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

                if (maxStacksByDefName == null)
                {
                    maxStacksByDefName = new Dictionary<string, int>();
                }

                if (maxStacksByCategoryDefName == null)
                {
                    maxStacksByCategoryDefName = new Dictionary<string, int>();
                }

                similarStackCount = Math.Max(1, similarStackCount);
                ClampMaxStacks(maxStacksByDefName);
                ClampMaxStacks(maxStacksByCategoryDefName);
            }
        }

        private static void ClampMaxStacks(Dictionary<string, int> values)
        {
            foreach (string key in new List<string>(values.Keys))
            {
                values[key] = Math.Max(1, values[key]);
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

        private bool TryFindClosestCategoryMaxStacksOverride(
            ThingDef def,
            out int value,
            out ThingCategoryDef source)
        {
            return TryFindCategoryMaxStacksOverride(def?.FirstThingCategory, out value, out source);
        }

        private bool TryFindCategoryMaxStacksOverride(
            ThingCategoryDef category,
            out int value,
            out ThingCategoryDef source)
        {
            for (int distance = 0; category != null && distance < 128; distance++)
            {
                if (maxStacksByCategoryDefName.TryGetValue(category.defName, out value))
                {
                    value = Math.Max(1, value);
                    source = category;
                    return true;
                }

                category = category.parent;
            }

            value = 1;
            source = null;
            return false;
        }
    }
}
