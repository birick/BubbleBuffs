﻿using Kingmaker.EntitySystem.Entities;
using Kingmaker.Items;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Mechanics;
using Kingmaker.Utility;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UniRx;
using BubbleBuffs.Extensions;
using Kingmaker.UnitLogic.Mechanics.Actions;
using Kingmaker.Blueprints;
using Kingmaker.UnitLogic.Buffs.Blueprints;
using Kingmaker.Designers.Mechanics.Buffs;
using Kingmaker.UnitLogic.FactLogic;
using Kingmaker.UnitLogic.Mechanics.Components;
using Kingmaker.UnitLogic.Abilities.Blueprints;

namespace BubbleBuffs {
    public class BufferState {
        private readonly Dictionary<BuffKey, BubbleBuff> BuffsByKey = new();
        //public List<Buff> buffList = new();
        public IEnumerable<BubbleBuff> BuffList;

        public bool Dirty = false;

        public Action OnRecalculated;

        public void RecalculateAvailableBuffs(List<UnitEntityData> Group) {
            Dirty = true;
            BuffsByKey.Clear();

            Main.Verbose("Recalculating full state");

            try {
                for (int characterIndex = 0; characterIndex < Group.Count; characterIndex++) {
                    UnitEntityData dude = Group[characterIndex];
                    Main.Verbose($"Looking at dude: ${dude.CharacterName}", "state");
                    foreach (var book in dude.Spellbooks) {
                        Main.Verbose($"  Looking at spellbook: {book.Blueprint.DisplayName}");
                        if (book.Blueprint.Spontaneous) {
                            for (int level = 1; level <= book.LastSpellbookLevel; level++) {
                                Main.Verbose($"    Looking at spont level {level}", "state");
                                ReactiveProperty<int> credits = new ReactiveProperty<int>(book.GetSpellsPerDay(level));
                                foreach (var spell in book.GetKnownSpells(level)) {
                                    Main.Verbose($"      Adding spontaneous buff: {spell.Name}", "state");
                                    AddBuff(dude, book, spell, null, credits, false, int.MaxValue, characterIndex);
                                }
                                foreach (var spell in book.GetCustomSpells(level)) {
                                    Main.Verbose($"      Adding spontaneous (customised) buff: {spell.Name}/{dude.CharacterName}", "state");
                                    AddBuff(dude, book, spell, null, credits, false, int.MaxValue, characterIndex);
                                }
                            }
                        } else {
                            foreach (var slot in book.GetAllMemorizedSpells()) {
                                Main.Verbose($"      Adding prepared buff: {slot.Spell.Name}", "state");
                                AddBuff(dude, book, slot.Spell, null, new ReactiveProperty<int>(1), true, int.MaxValue, characterIndex);
                            }
                        }
                    }
                }
            } catch (Exception ex) {
                Main.Error(ex, "finding spells");
            }

            try {
                for (int characterIndex = 0; characterIndex < Group.Count; characterIndex++) {
                    UnitEntityData dude = Group[characterIndex];
                    foreach (Ability ability in dude.Abilities.RawFacts) {
                        ItemEntity sourceItem = ability.SourceItem;
                        if (sourceItem == null || !sourceItem.IsSpendCharges) {
                            var credits = new ReactiveProperty<int>(500);
                            if (ability.Data.Resource != null) {
                                credits.Value = ability.Data.Resource.GetMaxAmount(dude);
                            }
                            AddBuff(dude, null, ability.Data, null, credits, true, int.MaxValue, characterIndex, Category.Ability);
                        }
                    }
                }
            } catch (Exception ex) {
                Main.Error(ex, "finding abilities:");
            }

            var list = new List<BubbleBuff>(BuffsByKey.Values);
            //list.Sort((a, b) => {
            //    return a.Name.CompareTo(b.Name);
            //});
            //Main.Log("Sorting buffs");
            BuffList = list;

            foreach (var buff in BuffList) {
                if (SavedState.Buffs.TryGetValue(buff.Key, out var fromSave)) {
                    buff.InitialiseFromSave(fromSave);
                }
                buff.SortProviders();
            }



            lastGroup.Clear();
            lastGroup.AddRange(Group.Select(x => x.UniqueId));
            InputDirty = false;


        }


        public SavedBufferState SavedState;

        public BufferState(SavedBufferState save) {
            this.SavedState = save;
        }

        internal void Recalculate(bool updateUi) {
            var group = Bubble.Group;
            if (InputDirty || GroupIsDirty(group)) {
                RecalculateAvailableBuffs(group);
            }

            foreach (var gbuff in BuffList)
                gbuff.Invalidate();
            foreach (var gbuff in BuffList)
                gbuff.Validate();
            foreach (var gbuff in BuffList)
                gbuff.OnUpdate?.Invoke();

            if (updateUi) {
                OnRecalculated?.Invoke();
            }

            Save();
        }

        public bool GroupIsDirty(List<UnitEntityData> group) {
            if (lastGroup.Count != group.Count)
                return true;

            foreach (var unit in group) {
                if (!lastGroup.Contains(unit.UniqueId))
                    return true;
            }

            return false;
        }

        public void Save() {
            static void updateSavedBuff(BubbleBuff buff, SavedBuffState save) {
                save.Blacklisted = buff.HideBecause(HideReason.Blacklisted);
                save.InGroup = buff.InGroup;
                for (int i = 0; i < Bubble.Group.Count; i++) {
                    if (buff.UnitWants(i)) {
                        save.Wanted.Add(Bubble.Group[i].UniqueId);
                    } else if (buff.UnitWantsRemoved(i)) {
                        save.Wanted.Remove(Bubble.Group[i].UniqueId);
                    }
                }
                foreach (var caster in buff.CasterQueue) {
                    if (!save.Casters.TryGetValue(caster.Key, out var state)) {
                        state = new SavedCasterState();
                        save.Casters[caster.Key] = state;
                    }
                    state.Banned = caster.Banned;
                    state.Cap = caster.CustomCap;
                    state.ShareTransmutation = caster.ShareTransmutation;
                    state.PowerfulChange = caster.PowerfulChange;
                }
            }

            foreach (var buff in BuffList) {
                var key = buff.Key;
                if (SavedState.Buffs.TryGetValue(key, out var save)) {
                    updateSavedBuff(buff, save);
                    if (save.Wanted.Empty() && !buff.HideBecause(HideReason.Blacklisted) ) {
                        SavedState.Buffs.Remove(key);
                    }
                } else if (buff.Requested > 0 || buff.HideBecause(HideReason.Blacklisted)) {
                    save = new();
                    save.Wanted = new HashSet<string>();
                    updateSavedBuff(buff, save);
                    SavedState.Buffs[key] = save;
                }
            }


            SavedState.Version = 1;
            using (var settingsWriter = File.CreateText(BubbleBuffSpellbookController.SettingsPath)) {
                JsonSerializer.CreateDefault().Serialize(settingsWriter, SavedState);
            }
        }

        private static Dictionary<Guid, bool> SpellsWithBeneficialBuffs = new();

        //private static Dictionary<Guid, List<ContextActionApplyBuff>> CachedBuffEffects;

        public void AddBuff(UnitEntityData dude, Kingmaker.UnitLogic.Spellbook book, AbilityData spell, AbilityData baseSpell, IReactiveProperty<int> credits, bool newCredit, int creditClamp, int charIndex, Category category = Category.Spell) {
            //if (spell.TargetAnchor == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityTargetAnchor.Point)
            //    Main.Log($"Rejecting {spell.Name} due to being cast-at-point");

            //bool anyTargets = Bubble.Group.Any(t => spell.CanTarget(new TargetWrapper(t)));
            //if (!anyTargets) {
            //    return;
            //} 


            if (spell.Blueprint.HasVariants) {
                var variantsComponent = spell.Blueprint.Components.First(c => typeof(AbilityVariants).IsAssignableFrom(c.GetType())) as AbilityVariants;
                foreach (var variant in variantsComponent.Variants) {
                    AbilityData data;
                    if (book == null) {
                        data = new AbilityData(variant, dude);
                    } else
                        data = new AbilityData(variant, book, spell.SpellLevel);
                    AddBuff(dude, book, data, spell, credits, false, creditClamp, charIndex, category);
                }
                return;
            }
            

            int clamp = int.MaxValue;
            if (spell.TargetAnchor == Kingmaker.UnitLogic.Abilities.Blueprints.AbilityTargetAnchor.Owner) {
                clamp = 1;
            }

            var key = new BuffKey(spell);
            if (BuffsByKey.TryGetValue(key, out var buff)) {
                buff.AddProvider(dude, book, spell, baseSpell, credits, newCredit, clamp, charIndex);
            } else {
                BlueprintAbility touchAbility = null;
                var appliedEffectList = spell.Blueprint.FlattenAllActions().OfType<ContextActionApplyBuff>().ToList();
                if (appliedEffectList.Empty()) {
                    touchAbility = spell.Blueprint.GetComponent<AbilityEffectStickyTouch>()?.TouchDeliveryAbility;
                    if (touchAbility != null)
                        appliedEffectList = touchAbility.FlattenAllActions().OfType<ContextActionApplyBuff>().ToList();
                }

                

                //bool all = true;
                //if (!SpellsWithBeneficialBuffs.ContainsKey(spell.Blueprint.AssetGuid.m_Guid)) {

                //    if (all || spell.Name == "Heal, Mass") {
                //        var beneficialBuffs = spell.Blueprint.GetBeneficialBuffs();

                //        bool bubbleRejects = beneficialBuffs.Empty();
                //        bool vekRejects = appliedEffectList.Empty();

                //        if (bubbleRejects != vekRejects) {
                //            Main.Log(" *** minority report");
                //        }
                //        Main.Log($"[{spell.Name} => Bubble rejects {bubbleRejects} vs vek rejects {vekRejects}");
                //    }
                //    SpellsWithBeneficialBuffs[spell.Blueprint.AssetGuid.m_Guid] = true;
                //}

                if (appliedEffectList.Empty())
                    return;


                bool isShort = false;
                isShort = !appliedEffectList.Any(buff => buff.Permanent || (buff.UseDurationSeconds && buff.DurationSeconds >= 60) || buff.DurationValue.Rate != DurationRate.Rounds);


                buff = new BubbleBuff(spell) {
                    BuffsApplied = appliedEffectList
                };

                if (spell.Blueprint.GetComponent<AbilityTargetsAround>() != null || touchAbility?.GetComponent<AbilityTargetsAround>() != null) {
                    buff.IsMass = true;
                }

                buff.Category = category;

                buff.SetHidden(HideReason.Short, isShort);

                if (dude != null) {
                    buff.AddProvider(dude, book, spell, baseSpell, credits, newCredit, clamp, charIndex);
                }

                BuffsByKey[key] = buff;
            }
        }


        private HashSet<string> lastGroup = new();
        internal bool InputDirty = true;

    }

}