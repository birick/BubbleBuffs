﻿using BubbleBuffs.Config;
using BubbleBuffs.Utilities;
using HarmonyLib;
using Kingmaker;
using Kingmaker.AreaLogic.Cutscenes;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.PubSubSystem;
using Kingmaker.UI;
using Kingmaker.UI.Common;
using Kingmaker.UI.Common.Animations;
using Kingmaker.UI.MVVM._PCView.ActionBar;
using Kingmaker.UI.MVVM._PCView.IngameMenu;
using Kingmaker.UI.MVVM._PCView.Other;
using Kingmaker.UI.MVVM._PCView.Party;
using Kingmaker.UI.MVVM._PCView.ServiceWindows.Spellbook.KnownSpells;
using Kingmaker.UI.MVVM._VM.Other;
using Kingmaker.UI.MVVM._VM.Tooltip.Bricks;
using Kingmaker.UI.MVVM._VM.Tooltip.Templates;
using Kingmaker.UI.MVVM._VM.Tooltip.Utils;
using Kingmaker.UnitLogic;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.Utility;
using Newtonsoft.Json;
using Owlcat.Runtime.UI.Controls.Button;
using Owlcat.Runtime.UI.Utility;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TMPro;
using UniRx;
using UnityEngine;
using UnityEngine.UI;
using BubbleBuffs.Extensions;

namespace BubbleBuffs {

    struct SpinnerButtons {
        public OwlcatButton up;
        public OwlcatButton down;
    }


    public class BubbleBuffSpellbookController : MonoBehaviour {
        private GameObject ToggleButton;
        private bool Buffing => PartyView.m_Hide;
        private GameObject MainContainer;
        private GameObject NoSpellbooksContainer;

        private bool WasMainShown = false;

        private PartyPCView PartyView;
        private SavedBufferState save;
        public BufferState state;
        private BuffExecutor Executor;
        private BufferView view;

        private GameObject Root;

        private bool WindowCreated = false;

        public static string SettingsPath => $"{ModSettings.ModEntry.Path}UserSettings/bubblebuff-{Game.Instance.Player.GameId}.json";

        public void CreateBuffstate() {
            if (File.Exists(SettingsPath)) {
                using (var settingsReader = File.OpenText(SettingsPath))
                using (var jsonReader = new JsonTextReader(settingsReader)) {
                    save = JsonSerializer.CreateDefault().Deserialize<SavedBufferState>(jsonReader);

                    if (save.Version == 0) {
                        MigrateSaveToV1();
                    }
                }
            } else {
                save = new SavedBufferState();
            }

            state = new(save);
            view = new(state);
            Executor = new(state);

            view.widgetCache = new();
            view.widgetCache.PrefabGenerator = () => {
                SpellbookKnownSpellPCView spellPrefab = null;
                var listPrefab = UIHelpers.SpellbookScreen.Find("MainContainer/KnownSpells");
                var spellsKnownView = listPrefab.GetComponent<SpellbookKnownSpellsPCView>();

                if (spellsKnownView != null)
                    spellPrefab = listPrefab.GetComponent<SpellbookKnownSpellsPCView>().m_KnownSpellView;
                else {
                    foreach (var component in UIHelpers.SpellbookScreen.gameObject.GetComponents<Component>()) {
                        if (component.GetType().FullName == "EnhancedInventory.Controllers.SpellbookController") {
                            Main.Verbose(" ** INSTALLING WORKAROUND FOR ENHANCED INVENTORY **");
                            var fieldHandle = component.GetType().GetField("m_known_spell_prefab", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            Main.Verbose($"Got field handle: {fieldHandle != null}");
                            spellPrefab = (SpellbookKnownSpellPCView)fieldHandle.GetValue(component);
                            Main.Verbose($"Found spellPrefab: {spellPrefab != null}");

                            break;
                        }
                    }
                }

                var spellRoot = GameObject.Instantiate(spellPrefab.gameObject);
                spellRoot.name = "BubbleSpellView";
                spellRoot.DestroyComponents<SpellbookKnownSpellPCView>();
                spellRoot.DestroyChildrenImmediate("Icon/Decoration", "Icon/MythicArtFrame", "Icon/Domain", "Icon/ArtArrowImage", "RemoveButton", "Level");

                return spellRoot;

            };
        }

        private void MigrateSaveToV1() {
            Dictionary<string, string> nameToId = new();
            foreach (var ch in Game.Instance.Player.AllCharacters) {
                nameToId[ch.CharacterName] = ch.UniqueId;
            }

            foreach (SavedBuffState s in save.Buffs.Values) {
                HashSet<string> newWanted = new(s.Wanted.Select(name => nameToId[name]));
                s.Wanted = newWanted;

                Dictionary<CasterKey, SavedCasterState> casters = new();
                foreach (var casterEntry in s.Casters) {
                    var key = new CasterKey {
                        Name = nameToId[casterEntry.Key.Name],
                        Spellbook = casterEntry.Key.Spellbook
                    };
                    casters[key] = casterEntry.Value;
                }
                s.Casters = casters;
            }
        }

        private static void FadeOut(GameObject obj) {
            obj.GetComponent<FadeAnimator>().DisappearAnimation();
        }
        private static void FadeIn(GameObject obj) {
            obj.GetComponent<FadeAnimator>().AppearAnimation();
        }


        private void Awake() {
            MainContainer = transform.Find("MainContainer").gameObject;
            NoSpellbooksContainer = transform.Find("NoSpellbooksContainer").gameObject;

            PartyView = UIHelpers.StaticRoot.Find("PartyPCView").gameObject.GetComponent<PartyPCView>();

            GameObject.Destroy(transform.Find("bubblebuff-toggle")?.gameObject);
            GameObject.Destroy(transform.Find("bubblebuff-root")?.gameObject);

            ToggleButton = GameObject.Instantiate(transform.Find("MainContainer/MetamagicButton").gameObject, transform);
            (ToggleButton.transform as RectTransform).anchoredPosition = new Vector2(1400, 0);
            ToggleButton.name = "bubblebuff-toggle";
            ToggleButton.GetComponentInChildren<TextMeshProUGUI>().text = "Buff Setup";

            {
                var button = ToggleButton.GetComponentInChildren<OwlcatButton>();
                button.OnLeftClick.RemoveAllListeners();
                button.OnLeftClick.AddListener(() => {
                    PartyView.HideAnimation(!Buffing);

                    if (Buffing) {
                        WasMainShown = MainContainer.activeSelf;
                        if (WasMainShown)
                            FadeOut(MainContainer);
                        else
                            FadeOut(NoSpellbooksContainer);
                        MainContainer.SetActive(false);
                        NoSpellbooksContainer.SetActive(false);
                        ShowBuffWindow();
                    } else {
                        Hide();
                        if (WasMainShown) {
                            FadeIn(MainContainer);
                            MainContainer.SetActive(true);
                        } else {
                            FadeIn(NoSpellbooksContainer);
                            NoSpellbooksContainer.SetActive(true);
                        }
                    }

                });
            }

            Root = new GameObject("bubblebuff-root", typeof(RectTransform));
            Root.SetActive(false);
            Root.transform.SetParent(transform);
            var rect = Root.transform as RectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.localPosition = Vector3.zero;
            var group = Root.AddComponent<CanvasGroup>();
            var fader = Root.AddComponent<FadeAnimator>();
        }

        internal void Hide() {
            FadeOut(Root);
            Root.SetActive(false);
        }

        private static GameObject MakeToggle(GameObject togglePrefab, Transform parent, float x, float y, string text, string name) {
            var toggle = GameObject.Instantiate(togglePrefab, parent);
            toggle.name = name;
            var blacklistRect = toggle.transform as RectTransform;
            blacklistRect.localPosition = Vector2.zero;
            blacklistRect.anchoredPosition = Vector2.zero;
            blacklistRect.anchorMin = new Vector2(x, y);
            blacklistRect.anchorMax = new Vector2(x, y);
            blacklistRect.pivot = new Vector2(0.5f, 0.5f);
            toggle.GetComponentInChildren<TextMeshProUGUI>().text = text;
            toggle.SetActive(true);
            return toggle;
        }

        private static Portrait MakePortrait(GameObject portraitPrefab, RectTransform groupRect, bool destroyHealth, GameObject expandButtonPrefab, Portrait[] portraitGroup, GameObject popout) {
            var portrait = GameObject.Instantiate(portraitPrefab, groupRect);
            var handle = new Portrait {
                GameObject = portrait
            };
            GameObject.Destroy(portrait.transform.Find("Health").gameObject);
            GameObject.Destroy(portrait.transform.Find("PartBuffView").gameObject);
            GameObject.Destroy(portrait.transform.Find("BuffMain").gameObject);
            GameObject.Destroy(portrait.transform.Find("EncumbranceIndicator").gameObject);
            GameObject.Destroy(portrait.transform.Find("Levelups").gameObject);
            GameObject.Destroy(portrait.transform.Find("Bark").gameObject);

            GameObject.Destroy(portrait.transform.Find("HitPoint").gameObject);
            if (!destroyHealth) {
                var labelPrefab = UIHelpers.SpellbookScreen.Find("MainContainer/MemorizingPanelContainer/MemorizingPanel/SubstituteContainer/Label").gameObject;
                var label = GameObject.Instantiate(labelPrefab, portrait.transform);
                label.SetActive(true);
                handle.Text = label.GetComponentInChildren<TextMeshProUGUI>();
                handle.Text.richText = true;
                handle.Text.lineSpacing = -15.0f;
                label.Rect().SetAnchor(0.5, 0.5, -.18, -.18);
                label.Rect().sizeDelta = new Vector2(50, 1);
            }

            GameObject.Destroy(portrait.transform.Find("Portrait").GetComponent<UnitPortraitPartView>());
            GameObject.Destroy(portrait.GetComponent<PartyCharacterPCView>());

            var portraitRect = portrait.transform.Find("Portrait") as RectTransform;
            var frameRect = portrait.transform.Find("Frame") as RectTransform;
            portraitRect.anchorMin = frameRect.anchorMin;
            portraitRect.anchorMax = frameRect.anchorMax;
            portraitRect.anchoredPosition = frameRect.anchoredPosition;
            portraitRect.sizeDelta = frameRect.sizeDelta;
            portrait.transform.localPosition = Vector3.zero;

            handle.SelectedMark = frameRect.Find("Selected").gameObject;
            handle.SelectedMark.DestroyComponents<Image>();
            handle.SelectedMark.ChildRect("Mark").anchoredPosition = new Vector2(0, 84);

            if (expandButtonPrefab != null) {
                var expand = GameObject.Instantiate(expandButtonPrefab, portrait.transform);
                expand.Rect().pivot = new Vector2(0.5f, 0.5f);
                expand.Rect().SetAnchor(0.5, 1);
                expand.GetComponent<OwlcatButton>().Interactable = true;
                handle.Expand = expand.GetComponent<OwlcatButton>();
                handle.Expand.OnLeftClick.AddListener(() => {
                    handle.SetExpanded(!handle.Expand.IsSelected);
                    if (handle.Expand.IsSelected) {
                        foreach (var p in portraitGroup)
                            if (p != handle)
                                p.SetExpanded(false);

                        popout.transform.SetParent(portraitRect);
                        popout.Rect().anchoredPosition = new Vector2(0, 18);
                        popout.Rect().pivot = new Vector2(0.5f, 0);
                        popout.Rect().SetAnchor(0.5, 1);
                        popout.SetActive(true);
                    } else {
                        popout.SetActive(false);
                    }
                });
                handle.SetExpanded(false);
            }

            handle.Image = portraitRect.Find("LifePortrait").gameObject.GetComponent<Image>();
            var overlay = GameObject.Instantiate(portraitRect.gameObject.ChildObject("LifePortrait"), portraitRect);
            handle.Overlay = overlay.GetComponent<Image>();
            //overlay.AddComponent<AnimatedOverlay>().target = handle.Overlay;
            handle.Overlay.gameObject.SetActive(false);
            handle.Overlay.rectTransform.anchorMax = new Vector2(1, 0.3f);
            handle.Overlay.sprite = null;
            handle.Overlay.color = new Color(0, 1, 0, 0.4f);
            handle.Button = frameRect.gameObject.GetComponent<OwlcatMultiButton>();

            return handle;
        }

        public ReactiveProperty<bool> ShowOnlyRequested = new ReactiveProperty<bool>(false);
        public ReactiveProperty<bool> ShowShort = new ReactiveProperty<bool>(false);
        public ReactiveProperty<bool> ShowHidden = new ReactiveProperty<bool>(false);
        public ReactiveProperty<string> NameFilter = new ReactiveProperty<string>("");
        public ButtonGroup<Category> CurrentCategory;

        private List<UnitEntityData> Group => Game.Instance.SelectionCharacter.ActualGroup;

        private void CreateWindow() {
            var staticRoot = UIHelpers.StaticRoot;

            var portraitPrefab = staticRoot.Find("PartyPCView/PartyCharacterView_01").gameObject;
            Main.Verbose("Got portrait prefab");
            var listPrefab = UIHelpers.SpellbookScreen.Find("MainContainer/KnownSpells");
            Main.Verbose("Got list prefab");
            var spellsKnownView = listPrefab.GetComponent<SpellbookKnownSpellsPCView>();

            Main.Verbose("Got spell prefab");
            var framePrefab = UIHelpers.MythicInfoView.Find("Window/MainContainer/MythicInfoProgressionView/Progression/Frame").gameObject;
            Main.Verbose("Got frame prefab");
            var expandButtonPrefab = UIHelpers.EncyclopediaView.Find("EncyclopediaPageView/HistoryManagerGroup/HistoryGroup/PreviousButton").gameObject;
            Main.Verbose("Got expandButton prefab");
            var toggleTransform = UIHelpers.SpellbookScreen.Find("MainContainer/KnownSpells/Toggle");
            if (toggleTransform == null)
                toggleTransform = UIHelpers.SpellbookScreen.Find("MainContainer/KnownSpells/TogglePossibleSpells");
            var togglePrefab = toggleTransform.gameObject;
            Main.Verbose("Got toggle prefab: ");
            buttonPrefab = UIHelpers.SpellbookScreen.Find("MainContainer/MetamagicContainer/Metamagic/Button").gameObject;
            Main.Verbose("Got button prefab: ");
            selectedPrefab = UIHelpers.CharacterScreen.Find("Menu/Button/Selected").gameObject;
            Main.Verbose("Got selected prefab");
            var nextPrefab = staticRoot.Find("PartyPCView/Background/Next").gameObject;
            Main.Verbose("Got next prefab");
            var prevPrefab = staticRoot.Find("PartyPCView/Background/Prev").gameObject;
            Main.Verbose("Got prev prefab");

            var content = Root.transform;
            Main.Verbose("got root.transform");

            view.listPrefab = listPrefab.gameObject;
            view.content = content;
            Main.Verbose("set view prefabs");

            MakeAddRemoveAllButtons(buttonPrefab, content);
            Main.Verbose("made add/remove all");


            view.MakeBuffsList();

            MakeFilters(togglePrefab, content);

            MakeGroupHolder(portraitPrefab, expandButtonPrefab, buttonPrefab, content);
            Main.Verbose("made group holder");

            MakeDetailsView(portraitPrefab, framePrefab, nextPrefab, prevPrefab, togglePrefab, expandButtonPrefab, content);
            Main.Verbose("made details view");


            ShowHidden.Subscribe<bool>(show => {
                RefreshFiltering();
            });
            ShowOnlyRequested.Subscribe<bool>(show => {
                RefreshFiltering();
            });
            ShowShort.Subscribe<bool>(show => {
                RefreshFiltering();
            });
            NameFilter.Subscribe<string>(val => {
                if (search.InputField.text != val)
                    search.InputField.text = val;
                RefreshFiltering();
            });



            view.currentSelectedSpell.Subscribe(val => {
                try {
                    HideCasterPopout?.Invoke();
                    if (view.currentSelectedSpell.HasValue && view.currentSelectedSpell.Value != null) {
                        var buff = view.Selected;

                        if (buff == null) {
                            currentSpellView.SetActive(false);
                            return;
                        }

                        currentSpellView.SetActive(true);
                        BubbleSpellView.BindBuffToView(buff, currentSpellView);

                        view.addToAll.SetActive(true);
                        view.removeFromAll.SetActive(true);

                        float actualWidth = (buff.CasterQueue.Count - 1) * castersHolder.GetComponent<HorizontalLayoutGroup>().spacing;
                        (castersHolder.transform as RectTransform).anchoredPosition = new Vector2(-actualWidth / 2.0f, 0);

                        view.Update();
                    } else {
                        currentSpellView.SetActive(false);

                        view.addToAll.SetActive(false);
                        view.removeFromAll.SetActive(false);

                        foreach (var caster in view.casterPortraits)
                            caster.GameObject.SetActive(false);

                        foreach (var portrait in view.targets) {
                            portrait.Image.color = Color.white;
                            portrait.Button.Interactable = true;
                        }
                    }
                } catch (Exception e) {
                    Main.Error(e, "SELECTING SPELL");
                }
            });
            view.OnUpdate = () => {
                UpdateDetailsView?.Invoke();
            };
            WindowCreated = true;
        }

        private void RegenerateWidgetCache(Transform listPrefab, SpellbookKnownSpellsPCView spellsKnownView) {
            if (view.widgetCache == null) {
            }
        }

        public static GameObject buttonPrefab;
        public static GameObject selectedPrefab;

        public static GameObject MakeButton(string title, Transform parent) {
            var button = GameObject.Instantiate(buttonPrefab, parent);
            button.GetComponentInChildren<TextMeshProUGUI>().text = title;
            var buttonRect = button.transform as RectTransform;
            buttonRect.anchorMin = new Vector2(0.5f, 0.5f);
            buttonRect.anchorMax = new Vector2(0.5f, 0.5f);
            buttonRect.pivot = new Vector2(1, 1);
            buttonRect.localPosition = Vector3.zero;
            buttonRect.anchoredPosition = Vector2.zero;
            return button;
        }

        public class ButtonGroup<T> {
            public ReactiveProperty<T> Selected = new();
            private readonly Transform content;

            public ButtonGroup(Transform content) {
                this.content = content;
            }

            public T Value {
                get => Selected.Value;
                set => Selected.Value = value;
            }

            public void Add(T value, string title) {
                var button = MakeButton(title, content);

                var selection = GameObject.Instantiate(selectedPrefab, button.transform);
                selection.SetActive(false);

                Selected.Subscribe<T>(s => {
                    selection.SetActive(EqualityComparer<T>.Default.Equals(s, value));
                });
                button.GetComponentInChildren<OwlcatButton>().Interactable = true;
                button.GetComponentInChildren<OwlcatButton>().OnLeftClick.AddListener(() => {
                    Selected.Value = value;
                });
            }

        }

        public static RectTransform MakeVerticalRect(string name, Transform parent) {
            var obj = new GameObject(name, typeof(RectTransform));
            obj.AddComponent<VerticalLayoutGroup>().childForceExpandHeight = false;
            var rect = obj.Rect();
            rect.SetParent(parent);
            rect.anchoredPosition3D = Vector3.zero;
            return rect;
        }

        private void MakeFilters(GameObject togglePrefab, Transform content) {

            var filterRect = MakeVerticalRect("filters", content);
            //filterToggles.AddComponent<Image>().color = Color.green;
            filterRect.anchorMin = new Vector2(0.13f, 0.1f);
            filterRect.anchorMax = new Vector2(0.215f, .455f);

            search = new SearchBar(filterRect, "...", false, "bubble-search-buff");
            GameObject showHidden = MakeToggle(togglePrefab, filterRect, 0.8f, .5f, "Show Hidden", "bubble-toggle-show-hidden");
            GameObject showShort = MakeToggle(togglePrefab, filterRect, .8f, .5f, "Show Short", "bubble-toggle-show-short");
            GameObject showOnlyRequested = MakeToggle(togglePrefab, filterRect, .8f, .5f, "Only Requested", "bubble-toggle-show-requested");

            search.InputField.onValueChanged.AddListener(val => {
                NameFilter.Value = val;
            });

            CurrentCategory = new ButtonGroup<Category>(filterRect);
            CurrentCategory.Selected.Subscribe<Category>(_ => RefreshFiltering());

            CurrentCategory.Add(Category.Spell, "Spells");
            CurrentCategory.Add(Category.Ability, "Abilities");
            CurrentCategory.Add(Category.Item, "Items");
            CurrentCategory.Add(Category.Consumable, "Consumables");

            ShowShort.BindToView(showShort);
            ShowHidden.BindToView(showHidden);
            ShowOnlyRequested.BindToView(showOnlyRequested);

            CurrentCategory.Selected.Value = Category.Spell;
        }

        private void RefreshFiltering() {
            if (state.BuffList == null)
                return;

            foreach (var buff in state.BuffList) {

                if (!view.buffWidgets.TryGetValue(buff.Key, out var widget) || widget == null)
                    continue;

                bool show = true;

                if (buff.Category != CurrentCategory.Value)
                    show = false;


                if (ShowOnlyRequested.Value && buff.Requested == 0)
                    show = false;

                if (NameFilter.Value.Length > 0) {
                    var filterString = NameFilter.Value.ToLower();
                    if (!buff.NameLower.Contains(filterString))
                        show = false;
                }

                if (!ShowHidden.Value && buff.HideBecause(HideReason.Blacklisted))
                    show = false;
                if (!ShowShort.value && buff.HideBecause(HideReason.Short))
                    show = false;

                widget.SetActive(show);
            }
        }

        private Action HideCasterPopout;
        private Action UpdateDetailsView;

        private void MakeDetailsView(GameObject portraitPrefab, GameObject framePrefab, GameObject nextPrefab, GameObject prevPrefab, GameObject togglePrefab, GameObject expandButtonPrefab, Transform content) {
            var detailsHolder = GameObject.Instantiate(framePrefab, content);
            var detailsRect = detailsHolder.GetComponent<RectTransform>();
            GameObject.Destroy(detailsHolder.transform.Find("FrameDecor").gameObject);
            detailsRect.localPosition = Vector2.zero;
            detailsRect.sizeDelta = Vector2.zero;
            detailsRect.anchorMin = new Vector2(0.25f, 0.30f);
            detailsRect.anchorMax = new Vector2(0.75f, 0.51f);

            currentSpellView = view.widgetCache.Get(detailsRect);

            currentSpellView.GetComponentInChildren<OwlcatButton>().Interactable = false;
            currentSpellView.SetActive(false);
            var currentSpellRect = currentSpellView.transform as RectTransform;
            currentSpellRect.anchorMin = new Vector2(.5f, .78f);
            currentSpellRect.anchorMax = new Vector2(.5f, .78f);

            castersHolder = new GameObject("CastersHolder", typeof(RectTransform));
            var castersRect = castersHolder.GetComponent<RectTransform>();
            castersRect.SetParent(detailsRect);
            castersRect.localPosition = Vector2.zero;
            castersRect.sizeDelta = Vector2.zero;
            castersRect.anchorMin = new Vector2(0.5f, 0.22f);
            castersRect.anchorMax = new Vector2(0.5f, 0.57f);
            castersRect.pivot = new Vector2(0.5f, 0.4f);
            castersRect.anchoredPosition = new Vector2(-(60 * totalCasters) / 2.0f, 0.0f);


            var castersHorizontalGroup = castersHolder.AddComponent<HorizontalLayoutGroup>();
            castersHorizontalGroup.spacing = 70;
            castersHorizontalGroup.childControlHeight = true;
            castersHorizontalGroup.childForceExpandHeight = true;

            //var promoteCaster = GameObject.Instantiate(prevPrefab, detailsRect);
            //var promoteRect = promoteCaster.transform as RectTransform;
            //promoteCaster.SetActive(true);
            //promoteRect.localPosition = Vector2.zero;
            //promoteRect.anchoredPosition = Vector2.zero;
            //promoteRect.anchorMin = new Vector2(0.5f, 0.2f);
            //promoteRect.anchorMax = new Vector2(0.5f, 0.2f);
            //promoteRect.pivot = new Vector2(0.5f, 0.5f);
            //promoteRect.anchoredPosition = new Vector2(castersRect.anchoredPosition.x + 30, 0);

            //var demoteCaster = GameObject.Instantiate(nextPrefab, detailsRect);
            //var demoteRect = demoteCaster.transform as RectTransform;
            //demoteCaster.SetActive(true);
            //demoteRect.localPosition = Vector2.zero;
            //demoteRect.anchoredPosition = Vector2.zero;
            //demoteRect.anchorMin = new Vector2(0.5f, 0.2f);
            //demoteRect.anchorMax = new Vector2(0.5f, 0.2f);
            //demoteRect.pivot = new Vector2(0.5f, 0.5f);
            //demoteRect.anchoredPosition = new Vector2(castersRect.anchoredPosition.x + 60, 0);

            ReactiveProperty<int> SelectedCaster = new ReactiveProperty<int>(-1);

            var actionBarView = UIHelpers.StaticRoot.Find("ActionBarPcView").GetComponent<ActionBarPCView>();
            var popout = GameObject.Instantiate(actionBarView.m_DragSlot.m_ConvertedView.gameObject, content);

            HideCasterPopout = () => {
                view.casterPortraits.ForEach(x => x.SetExpanded(false));
                popout.SetActive(false);
            };
            popout.DestroyComponents<ActionBarConvertedPCView>();
            //var popoutGrid = popout.GetComponent<GridLayoutGroup>();
            //popoutGrid.cellSize = new Vector2(90, 40);
            popout.DestroyComponents<GridLayoutGroup>();
            popout.Rect().anchoredPosition3D = Vector3.zero;
            popout.Rect().localPosition = Vector3.zero;
            popout.Rect().SetAnchor(0, 0);
            popout.SetActive(false);
            popout.ChildObject("Background").GetComponent<Image>().raycastTarget = true;

            var popLayout = popout.AddComponent<LayoutElement>();
            popLayout.preferredWidth = 400;

            var popVert = popout.AddComponent<VerticalLayoutGroup>();
            popVert.childControlWidth = false;
            popVert.childControlHeight = true;

            GameObject MakeLabel(string text) {
                var labelRoot = GameObject.Instantiate(togglePrefab, ToggleRect(popout).transform);
                Main.Verbose($"Label root: {labelRoot == null}");
                labelRoot.DestroyComponents<ToggleWorkaround>();
                labelRoot.DestroyChildren("Background");
                labelRoot.GetComponentInChildren<TextMeshProUGUI>().text = text;
                labelRoot.SetActive(true);

                return labelRoot;
            }

            var capLabel = MakeLabel("  Limit casts to");

            var blacklist = GameObject.Instantiate(togglePrefab, ToggleRect(popout).transform);
            blacklist.SetActive(true);
            blacklist.Rect().localPosition = Vector3.zero;
            blacklist.GetComponent<HorizontalLayoutGroup>().childControlWidth = true;
            blacklist.GetComponentInChildren<TextMeshProUGUI>().text = "Ban from casting";
            var blacklistToggle = blacklist.GetComponentInChildren<ToggleWorkaround>();

            var powerfulChange = GameObject.Instantiate(togglePrefab, ToggleRect(popout).transform);
            powerfulChange.SetActive(true);
            powerfulChange.Rect().localPosition = Vector3.zero;
            powerfulChange.GetComponent<HorizontalLayoutGroup>().childControlWidth = true;
            powerfulChange.GetComponentInChildren<TextMeshProUGUI>().text = "Use 'Powerful Change'";
            var powerfulChangeToggle = powerfulChange.GetComponentInChildren<ToggleWorkaround>();

            var shareTransmutation = GameObject.Instantiate(togglePrefab, ToggleRect(popout).transform);
            shareTransmutation.SetActive(true);
            shareTransmutation.Rect().localPosition = Vector3.zero;
            shareTransmutation.GetComponent<HorizontalLayoutGroup>().childControlWidth = true;
            shareTransmutation.GetComponentInChildren<TextMeshProUGUI>().text = "Allow 'Share Transmutation'";
            var shareTransmutationToggle = shareTransmutation.GetComponentInChildren<ToggleWorkaround>();

            MakeLabel("  <i>Arcane Resevoir resource is <b>not</b> tracked</i>");

            float capChangeScale = 0.7f;
            var decreaseCustomCap = GameObject.Instantiate(expandButtonPrefab, capLabel.transform);
            decreaseCustomCap.Rect().localScale = new Vector3(capChangeScale, capChangeScale, capChangeScale);
            decreaseCustomCap.Rect().pivot = new Vector2(.5f, .5f);
            decreaseCustomCap.Rect().SetRotate2D(90);
            decreaseCustomCap.Rect().anchoredPosition = Vector2.zero;
            decreaseCustomCap.SetActive(true);
            var decreaseCustomCapButton = decreaseCustomCap.GetComponent<OwlcatButton>();

            var capValueLabel = GameObject.Instantiate(togglePrefab.GetComponentInChildren<TextMeshProUGUI>().gameObject, capLabel.transform);
            var capValueText = capValueLabel.GetComponent<TextMeshProUGUI>();
            capValueText.text = "no limit";
            capValueLabel.AddComponent<LayoutElement>().preferredWidth = 80;

            var increaseCustomCap = GameObject.Instantiate(expandButtonPrefab, capLabel.transform);
            increaseCustomCap.Rect().pivot = new Vector2(.5f, .5f);
            increaseCustomCap.Rect().localScale = new Vector3(capChangeScale, capChangeScale, capChangeScale);
            increaseCustomCap.Rect().SetRotate2D(-90);
            increaseCustomCap.Rect().anchoredPosition = Vector2.zero;
            increaseCustomCap.SetActive(true);
            var increaseCustomCapButton = increaseCustomCap.GetComponent<OwlcatButton>();


            void AdjustCap(int delta) {
                var buff = view.currentSelectedSpell.Value;
                if (buff == null) return;
                if (SelectedCaster.Value < 0) return;

                buff.AdjustCap(SelectedCaster.Value, delta);
                state.Recalculate(true);
            }

            decreaseCustomCapButton.OnLeftClick.AddListener(() => {
                AdjustCap(-1);
            });
            increaseCustomCapButton.OnLeftClick.AddListener(() => {
                AdjustCap(1);
            });

            decreaseCustomCapButton.Interactable = false;
            increaseCustomCapButton.Interactable = false;

            view.casterPortraits = new Portrait[totalCasters];

            shareTransmutationToggle.onValueChanged.AddListener(allow => {
                if (SelectedCaster.Value >= 0 && view.Get(out var buff)) {
                    buff.CasterQueue[SelectedCaster.Value].ShareTransmutation = allow;
                    state.Recalculate(true);
                }
            });
            powerfulChangeToggle.onValueChanged.AddListener(allow => {
                if (SelectedCaster.Value >= 0 && view.Get(out var buff)) {
                    buff.CasterQueue[SelectedCaster.Value].PowerfulChange = allow;
                    state.Recalculate(true);
                }
            });

            blacklistToggle.onValueChanged.AddListener((blacklisted) => {
                var buff = view.currentSelectedSpell?.Value;
                if (buff == null)
                    return;
                if (SelectedCaster.Value < 0)
                    return;

                var caster = buff.CasterQueue[SelectedCaster.value];

                if (blacklisted != caster.Banned) {
                    caster.Banned = blacklisted;
                    state.Recalculate(true);
                }
            });

            for (int i = 0; i < totalCasters; i++) {
                var casterPortrait = MakePortrait(portraitPrefab, castersRect, false, expandButtonPrefab, view.casterPortraits, popout);
                view.casterPortraits[i] = casterPortrait;
                var textRoot = casterPortrait.Text.gameObject.transform.parent as RectTransform;
                textRoot.anchoredPosition = new Vector2(0, -200);
                casterPortrait.Text.fontSizeMax = 18;
                casterPortrait.Text.fontSize = 18;
                casterPortrait.Text.color = Color.black;
                casterPortrait.Text.gameObject.transform.parent.gameObject.SetActive(true);
                casterPortrait.Text.text = "12/12";
                var aspect = casterPortrait.GameObject.AddComponent<AspectRatioFitter>();
                aspect.aspectMode = AspectRatioFitter.AspectMode.HeightControlsWidth;
                aspect.aspectRatio = 0.75f;
                //casterPortrait.Button.m_CommonLayer.RemoveAt(1);
                int casterIndex = i;

                casterPortrait.Expand.OnLeftClick.AddListener(() => {
                    if (casterPortrait.Expand.IsSelected) {
                        SelectedCaster.Value = casterIndex;
                        UpdateDetailsView();
                    } else {
                        SelectedCaster.Value = -1;
                    }
                });
            }

            var groupRect = MakeVerticalRect("buff-group", detailsRect);
            groupRect.gameObject.SetActive(false);
            groupRect.SetAnchor(0.9f, 0.6f);

            var buffGroup = new ButtonGroup<BuffGroup>(groupRect);

            buffGroup.Add(BuffGroup.Long, "Normal");
            buffGroup.Add(BuffGroup.Important, "Important");
            buffGroup.Add(BuffGroup.Short, "Short");

            buffGroup.Selected.Subscribe<BuffGroup>(g => {
                if (view.Get(out var buff)) {
                    buff.InGroup = g;
                    state.Save();
                }
            });

            UpdateDetailsView = () => {
                bool hasBuff = view.Get(out var buff);

                groupRect.gameObject.SetActive(hasBuff);

                if (!hasBuff)
                    return;

                buffGroup.Selected.Value = buff.InGroup;


                if (SelectedCaster.Value >= 0 && popout.activeSelf) {
                    var who = buff.CasterQueue[SelectedCaster.value];
                    int actualCap = who.CustomCap < 0 ? who.MaxCap : who.CustomCap;
                    capValueText.text = $"{actualCap}/{who.MaxCap}";

                    blacklistToggle.isOn = who.Banned;
                    shareTransmutationToggle.isOn = who.ShareTransmutation;
                    powerfulChangeToggle.isOn = who.PowerfulChange;

                    var skidmarkable = who.spell.IsArcanistSpell && who.spell.Blueprint.School == Kingmaker.Blueprints.Classes.Spells.SpellSchool.Transmutation;
                    shareTransmutationToggle.interactable = skidmarkable;
                    powerfulChangeToggle.interactable = skidmarkable;

                    increaseCustomCapButton.Interactable = who.AvailableCredits < 100 && who.CustomCap != -1;
                    decreaseCustomCapButton.Interactable = who.AvailableCredits < 100 && who.CustomCap != 0;

                }

            };

            static GameObject ToggleRect(GameObject popout) {
                var toggleRect = new GameObject("caster-toggle", typeof(RectTransform));
                var toggleSize = toggleRect.AddComponent<LayoutElement>();
                toggleSize.preferredHeight = 40;
                var toggleHori = toggleRect.AddComponent<HorizontalLayoutGroup>();
                toggleHori.childAlignment = TextAnchor.MiddleCenter;
                toggleHori.childControlHeight = false;
                toggleHori.childControlWidth = false;
                toggleRect.transform.SetParent(popout.transform);
                toggleRect.transform.localPosition = Vector3.zero;
                return toggleRect;
            }
        }

        private void MakeAddRemoveAllButtons(GameObject buttonPrefab, Transform content) {
            view.addToAll = GameObject.Instantiate(buttonPrefab, content);
            view.addToAll.GetComponentInChildren<TextMeshProUGUI>().text = "Add To All";
            var addToAllRect = view.addToAll.transform as RectTransform;
            addToAllRect.anchorMin = new Vector2(0.48f, 0.308f);
            addToAllRect.anchorMax = new Vector2(0.48f, 0.31f);
            addToAllRect.pivot = new Vector2(1, 1);
            addToAllRect.localPosition = Vector3.zero;
            addToAllRect.anchoredPosition = Vector2.zero;

            view.removeFromAll = GameObject.Instantiate(buttonPrefab, content);
            view.removeFromAll.GetComponentInChildren<TextMeshProUGUI>().text = "Remove From All";
            var removeFromAllRect = view.removeFromAll.transform as RectTransform;
            removeFromAllRect.anchorMin = new Vector2(0.52f, 0.308f);
            removeFromAllRect.anchorMax = new Vector2(0.52f, 0.31f);
            removeFromAllRect.pivot = new Vector2(0, 1);
            removeFromAllRect.localPosition = Vector3.zero;
            removeFromAllRect.anchoredPosition = Vector2.zero;

            view.addToAll.GetComponentInChildren<OwlcatButton>().OnLeftClick.AddListener(() => {
                var buff = view.Selected;
                if (buff == null) return;

                for (int i = 0; i < Group.Count; i++) {
                    if (view.targets[i].Button.Interactable && !buff.UnitWants(i)) {
                        buff.SetUnitWants(i, true);
                    }
                }
                state.Recalculate(true);

            });
            view.removeFromAll.GetComponentInChildren<OwlcatButton>().OnLeftClick.AddListener(() => {
                var buff = view.Selected;
                if (buff == null) return;

                for (int i = 0; i < Group.Count; i++) {
                    if (buff.UnitWants(i)) {
                        buff.SetUnitWants(i, false);
                    }
                }
                state.Recalculate(true);
            });
        }

        private int totalCasters = 0;
        private GameObject currentSpellView;
        private SearchBar search;

        private void MakeGroupHolder(GameObject portraitPrefab, GameObject expandButtonPrefab, GameObject buttonPrefab, Transform content) {
            var groupHolder = new GameObject("GroupHolder", typeof(RectTransform));
            var groupRect = groupHolder.GetComponent<RectTransform>();
            groupRect.SetParent(content);
            groupRect.localPosition = Vector2.zero;
            groupRect.sizeDelta = Vector2.zero;

            float requiredWidthHalf = Group.Count * 0.033f;
            //groupRect.anchoredPosition = new Vector2(-(120 * Group.Count) / 2.0f, 0);

            var horizontalGroup = groupHolder.AddComponent<HorizontalLayoutGroup>();
            horizontalGroup.spacing = 124;
            horizontalGroup.childControlHeight = true;
            horizontalGroup.childForceExpandHeight = true;

            view.targets = new Portrait[Group.Count];


            //foreach (var dude in Group) {
            //    var copyFrom = GameObject.Instantiate(buttonPrefab, popout.transform);
            //    copyFrom.GetComponentInChildren<TextMeshProUGUI>().text = dude.CharacterName;
            //    copyFrom.GetComponent<OwlcatButton>().Interactable = true;
            //}


            for (int i = 0; i < Group.Count; i++) {
                Portrait portrait = MakePortrait(portraitPrefab, groupRect, true, null, view.targets, null); //expandButtonPrefab);

                var aspect = portrait.GameObject.AddComponent<AspectRatioFitter>();
                aspect.aspectMode = AspectRatioFitter.AspectMode.HeightControlsWidth;
                aspect.aspectRatio = 0.75f;

                portrait.Image.sprite = Group[i].Portrait.SmallPortrait;

                int personIndex = i;

                //portrait.Expand.OnLeftClick.AddListener(() => {
                //    if (portrait.Expand.IsSelected) {
                //        popout.transform.SetParent(portrait.Expand.transform);
                //        popout.Rect().anchoredPosition = new Vector2(0, 32);
                //        popout.Rect().SetAnchor(0.5, 0);
                //        for (int t = 0; t < Group.Count; t++) {
                //            if (personIndex != t)
                //                view.targets[t].SetExpanded(false);
                //        }
                //        popout.SetActive(true);
                //    } else {
                //        popout.SetActive(false);
                //    }
                //});


                portrait.Button.OnLeftClick.AddListener(() => {
                    var buff = view.Selected;
                    if (buff == null)
                        return;

                    if (!buff.CanTarget(personIndex))
                        return;

                    if (buff.UnitWants(personIndex)) {
                        buff.SetUnitWants(personIndex, false);
                    } else {
                        buff.SetUnitWants(personIndex, true);
                    }

                    try {
                        state.Recalculate(true);
                    } catch (Exception ex) {
                        Main.Error(ex, "Recalculating spell list?");
                    }

                });
                view.targets[i] = portrait;

                totalCasters += Group[i].Spellbooks?.Count() ?? 0;
            }
            groupRect.anchorMin = new Vector2(0.5f, 0.115f);
            groupRect.anchorMax = new Vector2(0.5f, 0.255f);
            groupRect.pivot = new Vector2(0f, 0.5f);

            float actualWidth = (Group.Count - 1) * horizontalGroup.spacing;
            groupRect.anchoredPosition = new Vector2(-actualWidth / 2.0f, 0);
        }

        private void ShowBuffWindow() {

            if (!WindowCreated) {
                try {
                    CreateWindow();
                } catch (Exception ex) {
                    Main.Error(ex, "Creating window?");
                }
            }
            state.Recalculate(true);
            RefreshFiltering();
            Root.SetActive(true);
            FadeIn(Root);
        }

        public void Destroy() {
            GameObject.Destroy(Root);
            GameObject.Destroy(ToggleButton);
        }

        private void OnDestroy() {
        }

        private GameObject castersHolder;



        internal void Execute(BuffGroup group) {
            UnitBuffPartView.StartSuppression();
            Main.Log("Executing?");
            Executor.Execute(group);
            Invoke("EndBuffPartViewSuppression", 1.0f);
        }



        internal void RevalidateSpells() {
            if (state.GroupIsDirty(Group)) {
                AbilityCache.Revalidate();
            }

            state.InputDirty = true;
        }

        public void EndBuffPartViewSuppression() {
            UnitBuffPartView.EndSuppresion();
        }

    }

    public class CasterCacheEntry {
        public Ability PowerfulChange;
        public Ability ShareTransmutation;
    }

    public class AbilityCache {

        public static Dictionary<string, CasterCacheEntry> CasterCache = new();

        public static void Revalidate() {
            Main.Verbose("Revalidating Caster Cache");
            CasterCache.Clear();
            foreach (var u in Bubble.Group) {
                var entry = new CasterCacheEntry {
                    PowerfulChange = u.Abilities.GetAbility(BubbleBlueprints.PowerfulChange),
                    ShareTransmutation = u.Abilities.GetAbility(BubbleBlueprints.ShareTransmutation)
                };
                CasterCache[u.UniqueId] = entry;
            }
        }
    }


    [HarmonyPatch(typeof(UnitBuffPartPCView), "DrawBuffs")]
    static class UnitBuffPartView {

        private static bool suppress;

        public static void StartSuppression() {
            suppress = true;
        }

        public static void EndSuppresion() {
            suppress = false;
            int count = toUpdate.Count;
            foreach (var view in toUpdate)
                view.DrawBuffs();
            toUpdate.Clear();
            Main.Verbose($"Suppressed {suppressed} draws across {count} views");
            suppressed = 0;
        }

        private static int suppressed = 0;

        private static HashSet<UnitBuffPartPCView> toUpdate = new();

        static bool Prefix(UnitBuffPartPCView __instance) {
            if (suppress) {
                suppressed++;
                toUpdate.Add(__instance);
                return false;
            }

            __instance.Clear();
            bool flag = __instance.ViewModel.Buffs.Count > 6;
            __instance.m_AdditionalTrigger.gameObject.SetActive(flag);
            int num = 0;
            foreach (BuffVM viewModel in __instance.ViewModel.Buffs) {
                BuffPCView widget = WidgetFactory.GetWidget<BuffPCView>(__instance.m_BuffView, true);
                widget.Bind(viewModel);
                if (flag && num >= 5) {
                    widget.transform.SetParent(__instance.m_AdditionalContainer, false);
                } else {
                    widget.transform.SetParent(__instance.m_MainContainer, false);
                }
                num++;
                __instance.m_BuffList.Add(widget);
            }

            return false;
        }

    }

    class TooltipTemplateBuffer : TooltipBaseTemplate {
        public class BuffResult {
            public BubbleBuff buff;
            public List<string> messages;
            public int count;
            public BuffResult(BubbleBuff buff) {
                this.buff = buff;
            }
        };
        private List<BuffResult> good = new();
        private List<BuffResult> bad = new();
        private List<BuffResult> skipped = new();

        public BuffResult AddBad(BubbleBuff buff) {
            BuffResult result = new(buff);
            result.messages = new();
            bad.Add(result);
            return result;
        }
        public BuffResult AddSkip(BubbleBuff buff) {
            BuffResult result = new(buff);
            skipped.Add(result);
            return result;
        }
        public BuffResult AddGood(BubbleBuff buff) {
            BuffResult result = new(buff);
            good.Add(result);
            return result;
        }

        public override IEnumerable<ITooltipBrick> GetHeader(TooltipTemplateType type) {
            yield return new TooltipBrickEntityHeader("BubbleBuff results", null);
            yield break;
        }
        public override IEnumerable<ITooltipBrick> GetBody(TooltipTemplateType type) {
            List<ITooltipBrick> elements = new();
            AddResultsNoMessages("Buffs Applied", elements, good);
            AddResultsNoMessages("Buffs Skipped", elements, skipped);

            if (!bad.Empty()) {
                elements.Add(new TooltipBrickTitle("Buffs Failed"));
                elements.Add(new TooltipBrickSeparator());

                foreach (var r in bad) {
                    elements.Add(new TooltipBrickIconAndName(r.buff.Spell.Icon, $"<b>{r.buff.Name}</b>", TooltipBrickElementType.Small));
                    foreach (var msg in r.messages)
                        elements.Add(new TooltipBrickText("   " + msg));

                }
            }

            return elements;
        }

        private void AddResultsNoMessages(string title, List<ITooltipBrick> elements, List<BuffResult> result) {
            if (!result.Empty()) {
                elements.Add(new TooltipBrickTitle(title));
                elements.Add(new TooltipBrickSeparator());
                foreach (var r in result) {
                    elements.Add(new TooltipBrickIconAndName(r.buff.Spell.Icon, $"<b>{r.buff.NameMeta}</b> x{r.count}", TooltipBrickElementType.Small));
                }
            }
        }
    }

    class ServiceWindowWatcher : IUIEventHandler {
        public void HandleUIEvent(UIEventType type) {
            GlobalBubbleBuffer.Instance.SpellbookController?.Hide();
        }
    }

    class SyncBubbleHud : MonoBehaviour {

        private GameObject bubbleHud => GlobalBubbleBuffer.Instance.bubbleHud;

        private void OnEnable() {
            bubbleHud?.SetActive(true);
        }
        private void OnDisable() {
            bubbleHud?.SetActive(false);
        }

    }

    class GlobalBubbleBuffer {
        public BubbleBuffSpellbookController SpellbookController;
        private ButtonSprites applyBuffsSprites;
        private ButtonSprites applyBuffsShortSprites;
        private ButtonSprites applyBuffsImportantSprites;
        private GameObject buttonsContainer;
        public GameObject bubbleHud;
        public GameObject hudLayout;

        internal void TryInstallUI() {
            Main.Verbose("Installing ui");
            Main.Verbose($"spellscreennull: {UIHelpers.SpellbookScreen == null}");
            var spellScreen = UIHelpers.SpellbookScreen.gameObject;
            Main.Verbose("got spell screen");

#if DEBUG
            RemoveOldController(spellScreen);
#endif

            if (spellScreen.transform.root.GetComponent<BubbleBuffGlobalController>() == null) {
                Main.Verbose("Creating new global controller");
                spellScreen.transform.root.gameObject.AddComponent<BubbleBuffGlobalController>();
            }

            if (spellScreen.GetComponent<BubbleBuffSpellbookController>() == null) {
                Main.Verbose("Creating new controller");
                SpellbookController = spellScreen.AddComponent<BubbleBuffSpellbookController>();
                SpellbookController.CreateBuffstate();
            } 

            Main.Verbose("loading sprites");
            if (applyBuffsSprites == null)
                applyBuffsSprites = ButtonSprites.Load("apply_buffs", new Vector2Int(95, 95));
            if (applyBuffsShortSprites == null)
                applyBuffsShortSprites = ButtonSprites.Load("apply_buffs_short", new Vector2Int(95, 95));
            if (applyBuffsImportantSprites == null)
                applyBuffsImportantSprites = ButtonSprites.Load("apply_buffs_important", new Vector2Int(95, 95));

            var staticRoot = Game.Instance.UI.Canvas.transform;
            Main.Verbose("got static root");
            hudLayout = staticRoot.Find("HUDLayout/").gameObject;
            Main.Verbose("got hud layout");
            if (hudLayout.GetComponent<SyncBubbleHud>() == null) {
                hudLayout.AddComponent<SyncBubbleHud>();
                Main.Verbose("installed hud sync");
            }

            Main.Verbose("Removing old bubble root");
            var oldBubble = hudLayout.transform.parent.Find("BUBBLEMODS_ROOT");
            if (oldBubble != null) {
                GameObject.Destroy(oldBubble.gameObject);
            }

            bubbleHud = GameObject.Instantiate(hudLayout, hudLayout.transform.parent);
            Main.Verbose("instantiated root");
            bubbleHud.name = "BUBBLEMODS_ROOT";
            var rect = bubbleHud.transform as RectTransform;
            rect.anchoredPosition = new Vector2(0, 96);
            rect.SetSiblingIndex(hudLayout.transform.GetSiblingIndex() + 1);
            Main.Verbose("set sibling index");

            bubbleHud.DestroyComponents<HUDLayout>();
            bubbleHud.DestroyComponents<UISectionHUDController>();

            GameObject.Destroy(rect.Find("CombatLog_New").gameObject);
            GameObject.Destroy(rect.Find("Console_InitiativeTrackerHorizontalPC").gameObject);
            GameObject.Destroy(rect.Find("IngameMenuView/CompassPart").gameObject);

            bubbleHud.ChildObject("IngameMenuView").DestroyComponents<IngameMenuPCView>();

            Main.Verbose("destroyed old stuff");

            var buttonPanelRect = rect.Find("IngameMenuView/ButtonsPart");
            Main.Verbose("got button panel");
            GameObject.Destroy(buttonPanelRect.Find("TBMMultiButton").gameObject);
            GameObject.Destroy(buttonPanelRect.Find("InventoryButton").gameObject);
            GameObject.Destroy(buttonPanelRect.Find("Background").gameObject);

            Main.Verbose("destroyed more old stuff");

            buttonsContainer = buttonPanelRect.Find("Container").gameObject;
            var buttonsRect = buttonsContainer.transform as RectTransform;
            buttonsRect.anchoredPosition = Vector2.zero;
            buttonsRect.sizeDelta = new Vector2(47.7f * 8, buttonsRect.sizeDelta.y);
            Main.Verbose("set buttons rect");

            buttonsContainer.GetComponent<GridLayoutGroup>().startCorner = GridLayoutGroup.Corner.LowerLeft;

            var prefab = buttonsContainer.transform.GetChild(0).gameObject;
            prefab.SetActive(false);

            int toRemove = buttonsContainer.transform.childCount;

            //Loop from 1 and destroy child[1] since we want to keep child[0] as our prefab, which is super hacky but.
            for (int i = 1; i < toRemove; i++) {
                GameObject.DestroyImmediate(buttonsContainer.transform.GetChild(1).gameObject);
            }

            void AddButton(string text, string tooltip, ButtonSprites sprites, Action act) {
                var applyBuffsButton = GameObject.Instantiate(prefab, buttonsContainer.transform);
                applyBuffsButton.SetActive(true);
                applyBuffsButton.GetComponentInChildren<OwlcatButton>().m_CommonLayer[0].SpriteState = new SpriteState {
                    pressedSprite = sprites.down,
                    highlightedSprite = sprites.hover,
                };
                applyBuffsButton.GetComponentInChildren<OwlcatButton>().OnLeftClick.AddListener(() => {
                    act();
                });
                applyBuffsButton.GetComponentInChildren<OwlcatButton>().SetTooltip(new TooltipTemplateSimple(text, tooltip), InfoCallMethod.None);

                applyBuffsButton.GetComponentInChildren<Image>().sprite = sprites.normal;

            }

            AddButton("Buff Normal!", "Try to cast spells set in the buff window (Normal)", applyBuffsSprites, () => GlobalBubbleBuffer.Execute(BuffGroup.Long));
            AddButton("Buff Important!", "Try to cast spells set in the buff window (Important)", applyBuffsImportantSprites, () => GlobalBubbleBuffer.Execute(BuffGroup.Important));
            AddButton("Buff Short!", "Try to cast spells set in the buff window (Short)", applyBuffsShortSprites, () => GlobalBubbleBuffer.Execute(BuffGroup.Short));


            Main.Verbose("Finished early ui setup");
        }

#if DEBUG
        private static void RemoveOldController<T>(GameObject on) {
            List<Component> toDelete = new();

            foreach (var component in on.GetComponents<Component>()) {
                if (component.GetType().FullName == typeof(T).FullName && component.GetType() != typeof(T)) {
                    var method = component.GetType().GetMethod("Destroy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                    method.Invoke(component, new object[] { });
                    toDelete.Add(component);
                }
            }

            int count = toDelete.Count;
            for (int i = 0; i < count; i++) {
                GameObject.Destroy(toDelete[0]);
            }

        }

        private static void RemoveOldController(GameObject spellScreen) {
            RemoveOldController<BubbleBuffSpellbookController>(spellScreen);
            RemoveOldController<BubbleBuffGlobalController>(spellScreen.transform.root.gameObject);
        }
#endif

        internal void SetButtonState(bool v) {
            buttonsContainer?.SetActive(v);
        }

        public static List<UnitEntityData> Group => Game.Instance.SelectionCharacter.ActualGroup;

        public static GlobalBubbleBuffer Instance;
        private static ServiceWindowWatcher UiEventSubscriber;
        private static SpellbookWatcher SpellMemorizeHandler;
        private static HideBubbleButtonsWatcher ButtonHiderHandler;

        public static void Install() {

            Instance = new();
            UiEventSubscriber = new();
            SpellMemorizeHandler = new();
            ButtonHiderHandler = new();
            EventBus.Subscribe(Instance);
            EventBus.Subscribe(UiEventSubscriber);
            EventBus.Subscribe(SpellMemorizeHandler);
            EventBus.Subscribe(ButtonHiderHandler);

        }

        public static void Execute(BuffGroup group) {
            Instance.SpellbookController.Execute(group);
        }


        public static void Uninstall() {
            EventBus.Unsubscribe(Instance);
            EventBus.Unsubscribe(UiEventSubscriber);
            EventBus.Unsubscribe(SpellMemorizeHandler);
            EventBus.Unsubscribe(ButtonHiderHandler);
        }
    }


    [Flags]
    public enum HideReason {
        Short = 1,
        Blacklisted = 2,
    };


    public class CasterKey {
        [JsonProperty]
        public string Name;
        [JsonProperty]
        public Guid Spellbook;

        public override bool Equals(object obj) {
            return obj is CasterKey key &&
                   Name == key.Name &&
                   Spellbook.Equals(key.Spellbook);
        }

        public override int GetHashCode() {
            int hashCode = -1362747006;
            hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(Name);
            hashCode = hashCode * -1521134295 + Spellbook.GetHashCode();
            return hashCode;
        }
    }
     public enum Category {
        Spell,
        Ability,
        Item,
        Consumable,
    }

    public enum BuffGroup {
        Long,
        Short,
        Important,
    }


    class ButtonSprites {
        public Sprite normal;
        public Sprite hover;
        public Sprite down;

        public static ButtonSprites Load(string name, Vector2Int size) {
            return new ButtonSprites {
                normal = AssetLoader.LoadInternal("icons", $"{name}_normal.png", size),
                hover = AssetLoader.LoadInternal("icons", $"{name}_hover.png", size),
                down = AssetLoader.LoadInternal("icons", $"{name}_down.png", size),
            };
        }
    }


    public static class ReactiveBindings {

        public static void BindToView(this IReactiveProperty<bool> prop, GameObject toggle) {
            var view = toggle.GetComponentInChildren<ToggleWorkaround>();
            prop.Subscribe<bool>(val => {
                if (view.isOn != val) {
                    view.isOn = val;
                }
            });

            view.onValueChanged.AddListener(val => {
                if (prop.Value != val)
                    prop.Value = val;
            });

        }

    }
    class Portrait {
        public Image Image;
        public OwlcatMultiButton Button;
        public GameObject GameObject;
        public GameObject SelectedMark;
        public TextMeshProUGUI Text;
        public OwlcatButton Expand;
        public Image Overlay;

        public void ExpandOff() {
            Expand.IsSelected = false;
        }

        internal void SetExpanded(bool selected) {
            Expand.IsSelected = selected;
            Expand.gameObject.ChildRect("Image").eulerAngles = new Vector3(0, 0, Expand.IsSelected ? 90 : -90);
        }

        public RectTransform Transform { get { return GameObject.transform as RectTransform; } }
    }

    class BubbleSpellView {
        public static void BindBuffToView(BubbleBuff buff, GameObject view) {
            view.ChildObject("Name/NameLabel").GetComponent<TextMeshProUGUI>().text = buff.Name;
            view.ChildObject("Icon/IconImage").GetComponent<Image>().sprite = buff.Spell.Blueprint.Icon;
            var metamagicContainer = view.ChildObject("Metamagic");
            if (buff.Spell.IsMetamagicked()) {
                for (int i = 0; i < metamagicContainer.transform.childCount; i++) {
                    var icon = metamagicContainer.transform.GetChild(i).gameObject;
                    if (i < buff.Metamagics.Length) {
                        icon.SetActive(true);
                        icon.GetComponent<Image>().sprite = buff.Metamagics[i].SpellIcon();
                    } else
                        icon.SetActive(false);
                }
                metamagicContainer.SetActive(true);
            } else
                metamagicContainer.SetActive(false);

        }
    }

    class BubbleProfiler : IDisposable {

        private readonly Stopwatch watch = new();
        private readonly string name;


        public BubbleProfiler(string name) {
            this.name = name;
            watch.Start();
        }
        public void Dispose() {
            watch.Stop();
            Main.Verbose($">>> {name} => {watch.Elapsed.TotalSeconds}s");
        }
    }

    class WidgetCache {
        public int Hits;
        public int Misses;
        private GameObject prefab;
        private readonly List<GameObject> cache = new();

        public Func<GameObject> PrefabGenerator;

        public void ResetStats() {
            Hits = 0;
            Misses = 0;
        }

        public WidgetCache() { }

        public GameObject Get(Transform parent) {
            if (prefab == null) {
                prefab = PrefabGenerator.Invoke();
                if (prefab == null)
                    throw new Exception("null prefab in widget cache");
            }
            GameObject ret;
            if (cache.Empty()) {
                ret = GameObject.Instantiate(prefab, parent);
                Misses++;
            } else {
                Hits++;
                ret = cache.Last();
                ret.transform.SetParent(parent);
                cache.RemoveLast();
            }
            ret.SetActive(true);
            return ret;
        }

        public void Return(IEnumerable<GameObject> widgets) {
            //cache.AddRange(widgets);
        }

    }

    class BufferView {
        public Dictionary<BuffKey, GameObject> buffWidgets = new();

        public GameObject buffWindow;
        public GameObject removeFromAll;
        public GameObject addToAll;
        public Portrait[] targets;
        private BufferState state;
        public Portrait[] casterPortraits;

        public GameObject listPrefab;
        public Transform content;

        public WidgetCache widgetCache;

        public BufferView(BufferState state) {
            this.state = state;
            state.OnRecalculated = Update;
        }

        public void MakeBuffsList() {
            Main.Verbose("here");
            if (!state.Dirty)
                return;
            state.Dirty = false;
            Main.Verbose("state was dirty");

            widgetCache.Return(buffWidgets.Values);
            Main.Verbose("returned widget cache");
            GameObject.Destroy(content.Find("AvailableBuffList")?.gameObject);
            Main.Verbose("destroyed old buff list");
            buffWidgets.Clear();
            Main.Verbose("cleared widgets");

            var availableBuffs = GameObject.Instantiate(listPrefab.gameObject, content);
            availableBuffs.transform.SetAsFirstSibling();
            Main.Verbose("made new buff list");
            availableBuffs.name = "AvailableBuffList";
            availableBuffs.GetComponentInChildren<GridLayoutGroupWorkaround>().constraintCount = 4;
            Main.Verbose("set constraint count");
            var listRect = availableBuffs.transform as RectTransform;
            listRect.localPosition = Vector2.zero;
            listRect.sizeDelta = Vector2.zero;
            listRect.anchorMin = new Vector2(0.125f, 0.5f);
            listRect.anchorMax = new Vector2(0.875f, 0.9f);
            GameObject.Destroy(listRect.Find("Toggle")?.gameObject);
            GameObject.Destroy(listRect.Find("TogglePossibleSpells")?.gameObject);
            GameObject.Destroy(listRect.Find("ToggleAllSpells")?.gameObject);
            GameObject.Destroy(listRect.Find("ToggleMetamagic")?.gameObject);
            var scrollContent = availableBuffs.transform.Find("StandardScrollView/Viewport/Content");
            Main.Verbose("got scroll content");
            Main.Verbose($"destroying old stuff: {scrollContent.childCount}");
            int toDestroy = scrollContent.childCount;
            for (int i = 0; i < toDestroy; i++) {
                GameObject.DestroyImmediate(scrollContent.GetChild(0).gameObject);
            }

            Main.Verbose($"destroyed old stuff: {scrollContent.childCount}");
            //widgetListDrawHandle = buffWidgetList.DrawEntries<IWidgetView>(models, new List<IWidgetView> { spellPrefab });

            OwlcatButton previousSelection = null;
            widgetCache.ResetStats();
            using (new BubbleProfiler("making widgets")) {
                foreach (var buff in state.BuffList) {
                    GameObject widget = widgetCache.Get(scrollContent);
                    var button = widget.GetComponent<OwlcatButton>();
                    button.OnHover.RemoveAllListeners();
                    button.OnHover.AddListener(hover => {
                        PreviewReceivers(hover ? buff : null);
                    });
                    button.OnSingleLeftClick.AddListener(() => {
                        if (previousSelection != null && previousSelection != button) {
                            previousSelection.SetSelected(false);
                        }
                        if (!button.IsSelected) {
                            button.SetSelected(true);
                        }
                        currentSelectedSpell.Value = buff;
                        previousSelection = button;
                    });
                    var label = widget.ChildObject("School/SchoolLabel").GetComponent<TextMeshProUGUI>();
                    var textImage = widget.ChildObject("Name/BackgroundName").GetComponent<Image>();
                    buff.OnUpdate = () => {
                        if (widget == null)
                            return;
                        var (availNormal, availSelf) = buff.AvailableAndSelfOnly;
                        if (availNormal < 100)
                            label.text = $"casting: {buff.Fulfilled}/{buff.Requested} + available: {availNormal}+{availSelf}";
                        else
                            label.text = $"casting: {buff.Fulfilled}/{buff.Requested} + available: at will";
                        if (buff.Requested > 0 && buff.Fulfilled != buff.Requested) {
                            textImage.color = Color.red;
                        } else {
                            textImage.color = Color.white;
                        }
                    };
                    BubbleSpellView.BindBuffToView(buff, widget);
                    widget.ChildObject("School").SetActive(true);
                    widget.SetActive(true);

                    buffWidgets[buff.Key] = widget;
                }
            }

            Main.Verbose($"Widget cache: created={widgetCache.Hits + widgetCache.Misses}");

            foreach (var buff in state.BuffList) {
                buff.OnUpdate();
            }
        }

        public void Update() {
            if (state.Dirty) {
                try {
                    MakeBuffsList();
                } catch (Exception ex) {
                    Main.Error(ex, "revalidating dirty");
                }
            }

            if (currentSelectedSpell.Value == null)
                return;

            PreviewReceivers(null);
            UpdateCasterDetails(Selected);
            OnUpdate?.Invoke();
        }

        public Action OnUpdate;

        Color massGoodColor = new Color(0, 1, 0, 0.4f);
        Color massBadColor = new Color(1, 1, 0, 0.4f);

        public void PreviewReceivers(BubbleBuff buff) {
            if (buff == null && currentSelectedSpell.Value != null)
                buff = Selected;

            for (int p = 0; p < Bubble.Group.Count; p++)
                UpdateTargetBuffColor(buff, p);
        }

        private void UpdateTargetBuffColor(BubbleBuff buff, int i) {
            var portrait = targets[i].Image;
            targets[i].Button.Interactable = true;
            if (buff == null) {
                portrait.color = Color.white;
                return;
            }
            bool isMass = false;
            bool massGood = false;

            if (buff.IsMass && buff.Requested > 0) {
                isMass = true;
                if (buff.Fulfilled > 0)
                    massGood = true;
            }


            if (isMass && !buff.UnitWants(i)) {
                targets[i].Overlay.gameObject.SetActive(true);
                targets[i].Overlay.color = massGood ? massGoodColor : massBadColor;
            } else {
                targets[i].Overlay.gameObject.SetActive(false);
            }


            if (!buff.CanTarget(i)) {
                portrait.color = Color.red;
                targets[i].Button.Interactable = false;
                targets[i].SelectedMark.SetActive(false);

            } else if (buff.UnitWants(i)) {
                targets[i].SelectedMark.SetActive(true);
                if (buff.UnitGiven(i)) {
                    portrait.color = Color.green;
                } else {
                    portrait.color = Color.yellow;
                }
            } else {
                targets[i].SelectedMark.SetActive(false);
                portrait.color = Color.gray;
            }
        }

        private void UpdateCasterDetails(BubbleBuff buff) {
            for (int i = 0; i < casterPortraits.Length; i++) {
                casterPortraits[i].GameObject.SetActive(i < buff.CasterQueue.Count);
                if (i < buff.CasterQueue.Count) {
                    var who = buff.CasterQueue[i];
                    casterPortraits[i].Image.sprite = targets[who.CharacterIndex].Image.sprite;
                    var bookName = who.book?.Blueprint.Name ?? "";
                    if (who.AvailableCredits < 100)
                        casterPortraits[i].Text.text = $"{who.spent}+{who.AvailableCredits}\n<i>{bookName}</i>";
                    else
                        casterPortraits[i].Text.text = $"at will\n<i>{bookName}</i>";
                    casterPortraits[i].Text.fontSize = 12;
                    casterPortraits[i].Text.outlineWidth = 0;
                    casterPortraits[i].Image.color = who.Banned ? Color.red : Color.white;
                }
            }
            addToAll.GetComponentInChildren<OwlcatButton>().Interactable = buff.Requested != Bubble.Group.Count;
            removeFromAll.GetComponentInChildren<OwlcatButton>().Interactable = buff.Requested > 0;
        }

        public IReactiveProperty<BubbleBuff> currentSelectedSpell = new ReactiveProperty<BubbleBuff>();

        public bool Get(out BubbleBuff buff) {
            buff = currentSelectedSpell.Value;
            if (currentSelectedSpell.Value == null)
                return false;
            return true;
        }

        public BubbleBuff Selected {
            get {
                if (currentSelectedSpell == null)
                    return null;
                return currentSelectedSpell.Value;
            }
        }
    }

    static class Bubble {
        public static List<UnitEntityData> Group => Game.Instance.SelectionCharacter.ActualGroup;
    }


    internal class SpellbookWatcher : ISpellBookUIHandler, IAreaHandler, ILevelUpCompleteUIHandler, IPartyChangedUIHandler {
        public static void Safely(Action a) {
            try {
                a.Invoke();
            } catch (Exception ex) {
                Main.Error(ex, "");
            }
        }
        public void HandleForgetSpell(AbilityData data, UnitDescriptor owner) {
            Safely(() => GlobalBubbleBuffer.Instance.SpellbookController?.RevalidateSpells());
        }

        public void HandleLevelUpComplete(UnitEntityData unit, bool isChargen) {
            Safely(() => GlobalBubbleBuffer.Instance.SpellbookController?.RevalidateSpells());
        }

        public void HandleMemorizedSpell(AbilityData data, UnitDescriptor owner) {
            Safely(() => GlobalBubbleBuffer.Instance.SpellbookController?.RevalidateSpells());
        }

        public void HandlePartyChanged() {
            Main.Verbose("Hello?");
            Safely(() => GlobalBubbleBuffer.Instance.SpellbookController?.RevalidateSpells());
        }

        public void OnAreaDidLoad() {
            Main.Verbose("Loaded area...");
            GlobalBubbleBuffer.Instance.TryInstallUI();
            AbilityCache.Revalidate();
        }

        public void OnAreaBeginUnloading() { }

    }
    internal class HideBubbleButtonsWatcher : ICutsceneHandler, IPartyCombatHandler {
        public void HandleCutscenePaused(CutscenePlayerData cutscene, CutscenePauseReason reason) { }

        public void HandleCutsceneRestarted(CutscenePlayerData cutscene) { }

        public void HandleCutsceneResumed(CutscenePlayerData cutscene) { }

        public void HandleCutsceneStarted(CutscenePlayerData cutscene, bool queued) { }

        public void HandleCutsceneStopped(CutscenePlayerData cutscene) { }

        public void HandlePartyCombatStateChanged(bool inCombat) { }
    }

    static class BubbleBlueprints {
        public static BlueprintAbility ShareTransmutation => Resources.GetBlueprint<BlueprintAbility>("749567e4f652852469316f787921e156");
        public static BlueprintAbility PowerfulChange => Resources.GetBlueprint<BlueprintAbility>("a45f3dae9c64ec848b35f85568f4b220");
    }
}