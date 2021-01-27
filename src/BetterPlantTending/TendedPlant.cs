﻿using Klei.AI;

namespace BetterPlantTending
{
    public abstract class TendedPlant : KMonoBehaviour
    {
        public const string FARM_TINKER_EFFECT_ID = "FarmTinker";
        public const string DIVERGENT_CROP_TENDED_EFFECT_ID = "DivergentCropTended";
        public const string DIVERGENT_CROP_TENDED_WORM_EFFECT_ID = "DivergentCropTendedWorm";

        protected static readonly string[] CropTendingEffects = new string[] {
            FARM_TINKER_EFFECT_ID,
#if EXPANSION1
            DIVERGENT_CROP_TENDED_EFFECT_ID,
            DIVERGENT_CROP_TENDED_WORM_EFFECT_ID,
#endif
        };

        [MyCmpReq]
        protected Effects effects;

        protected virtual bool ApplyModifierOnEffectRemoved => false;

        protected override void OnSpawn()
        {
            base.OnSpawn();
            Subscribe((int)GameHashes.EffectAdded, OnEffectChanged);
            if (ApplyModifierOnEffectRemoved)
                Subscribe((int)GameHashes.EffectRemoved, OnEffectChanged);
        }

        protected override void OnCleanUp()
        {
            Unsubscribe((int)GameHashes.EffectAdded, OnEffectChanged);
            if (ApplyModifierOnEffectRemoved)
                Unsubscribe((int)GameHashes.EffectRemoved, OnEffectChanged);
            base.OnCleanUp();
        }

        private void OnEffectChanged(object data)
        {
            ApplyModifier();
        }

        public virtual void ApplyModifier()
        {
        }
    }
}
