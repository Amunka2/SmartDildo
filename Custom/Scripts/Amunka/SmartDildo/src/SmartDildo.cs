using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SimpleJSON;
using UnityEngine;

// ReSharper disable UseCollectionExpression
// ReSharper disable MergeIntoPattern
// ReSharper disable Unity.PerformanceCriticalCodeInvocation
// ReSharper disable CanSimplifyDictionaryLookupWithTryGetValue
// ReSharper disable ConvertClosureToMethodGroup
// ReSharper disable ArrangeObjectCreationWhenTypeEvident

namespace SmartDildo.Custom.Scripts.Amunka.SmartDildo
{
    public sealed class SmartDildo : MVRScript
    {
        // Internal class to store settings per asset
        private class AssetState
        {
            public string BonePath;
            public bool HardParenting;
        }

        // State memory using a composite key "atomUid|assetName"
        private readonly Dictionary<string, AssetState> _assetStateMemory = new Dictionary<string, AssetState>();
        private string _currentAssetNameForAtom;

        // --- NEW --- Flag to prevent callbacks during destruction
        private bool _isBeingDestroyed;


        // UI elements
        private new JSONStorableBool _enabledJson;
        private JSONStorableStringChooser _cuaChoiceJson;
        private JSONStorableStringChooser _boneChoiceJson;
        private JSONStorableBool _syncPositionXJson;
        private JSONStorableBool _syncPositionYJson;
        private JSONStorableBool _syncPositionZJson;
        private JSONStorableBool _syncRotationJson;
        private JSONStorableBool _followMovementJson;
        private JSONStorableFloat _syncSpeedJson;
        private JSONStorableBool _debugModeJson;
        private JSONStorableBool _hardParentingJson;

        // New offset UI elements
        private JSONStorableFloat _offsetXJson;
        private JSONStorableFloat _offsetYJson;
        private JSONStorableFloat _offsetZJson;
        private JSONStorableFloat _offsetRotXJson;
        private JSONStorableFloat _offsetRotYJson;
        private JSONStorableFloat _offsetRotZJson;
        private JSONStorableFloat _offsetMultiplierJson;


        // Atom switching flag
        private bool _switchingAtoms;

        // UI control references for offset controls
        private UIDynamicButton _resetOffsetButton;
        private UIDynamicSlider _offsetMultiplierSlider;

        private float _dildoLength;

        private JSONStorableFloat _pullFactorJson;
        private JSONStorableFloat _pushFactorJson;


        // Pose management
        private readonly Dictionary<string, JSONClass> _savedPoses = new Dictionary<string, JSONClass>();
        private JSONStorableStringChooser _poseChoiceJson;
        private JSONStorableString _newPoseNameJson;
        private UIDynamicButton _savePoseButton;
        private UIDynamicButton _loadPoseButton;
        private UIDynamicButton _deletePoseButton;
        private UIDynamicTextField _newPoseNameTextField;
        private UIDynamicButton _replacePoseButton;
        private UIDynamicPopup _poseChoicePopup;
        private bool _poseUIVisible = true;

        // Reference to Hard Parenting Toggle for hiding/showing
        private UIDynamicToggle _hardParentingToggle;

        private Atom _cuaAtom;
        private readonly List<string> _cuaAtomNames = new List<string>();
        private Transform _selectedBone;
        private Transform _controlPoint;
        private bool _initialized;

        private GameObject _mediumObject;
        private Transform _mediumTransform;

        private Quaternion _targetRotation;

        private CustomUnityAssetLoader _loader;
        private Transform _cuaRoot;
        private JSONStorableStringChooser _assetNameChooser; // Used to detect asset changes

        private bool _needsReconnection;
        private float _reconnectionTimer;
        private const float ReconnectionInterval = 0.025f;

        private string _lastSelectedBonePath;

        private JSONClass _savedJson;
        private bool _needsRestoration;


        private readonly Dictionary<string, string> _boneDisplayToPath = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _bonePathToDisplay = new Dictionary<string, string>();

        private bool _addedRigidbody;
        private Rigidbody _boneRigidbody;
        private bool _wasKinematic;
        private bool _hadRigidbody;

        private Rigidbody _controlRigidbody;
        private bool _addedControlRigidbody;
        private bool _hadControlRigidbody;
        private bool _wasControlKinematic;

        public override void Init()
        {
            try
            {
                if (containingAtom.uid == "Person")
                {
                    SuperController.LogError("SmartDildo: Invalid atom type. Only Non-Person atoms are supported");
                    return;
                }
                
                pluginLabelJSON.val = "Bone Control";

                InitializeUI();
                PopulateAtomList();

                SuperController.singleton.onAtomAddedHandlers += OnAtomAdded;
                SuperController.singleton.onAtomRemovedHandlers += OnAtomRemoved;

                _controlPoint = containingAtom.mainController.transform;

                Transform tipTransform = null;
                Transform rootTransform = null;

                foreach (Transform child in containingAtom.transform)
                {
                    if (child.name.ToLower().Contains("tip")) tipTransform = child;
                    if (child.name.ToLower().Contains("root") || child.name.ToLower().Contains("base")) rootTransform = child;
                }

                if (tipTransform == null || rootTransform == null)
                {
                    var collider = containingAtom.GetComponentInChildren<Collider>();
                    if (collider != null)
                    {
                        _dildoLength = collider.bounds.size.z;
                        if (_debugModeJson != null && _debugModeJson.val) SuperController.LogMessage("Detected dildo length of " + _dildoLength);
                    }
                }

                _initialized = true;
                if (_debugModeJson != null && _debugModeJson.val) SuperController.LogMessage("Plugin Init completed.");
            }
            catch (Exception e)
            {
                SuperController.LogError("Exception caught in SmartDildo.Init: " + e);
            }
        }

        public void OnEnable()
        {
            try
            {
                // Reset destruction flag when re-enabled
                _isBeingDestroyed = false;

                if (!_initialized) return;
                if (_debugModeJson != null && _debugModeJson.val) SuperController.LogMessage("Plugin OnEnable called.");

                if (_savedJson != null)
                {
                    if (_debugModeJson != null && _debugModeJson.val) SuperController.LogMessage("OnEnable: Flagging for restoration.");
                    _needsRestoration = true;
                }
                else if (_selectedBone != null)
                {
                    if (_debugModeJson != null && _debugModeJson.val) SuperController.LogMessage("OnEnable: Re-applying current RB state.");
                    ApplyCurrentRigidbodyState();
                    HandleOffsetToggleChange();
                }
            }
            catch (Exception e)
            {
                SuperController.LogError($"{nameof(SmartDildo)}.{nameof(OnEnable)}: " + e);
            }
        }

        public void OnDisable()
        {
            try
            {
                // Set destruction flag in case this is part of unloading the scene
                _isBeingDestroyed = true;

                if (_initialized && _debugModeJson != null && _debugModeJson.val) SuperController.LogMessage("Plugin OnDisable called. Restoring RB states.");
                // Restore RB states when natively disabled
                RestoreRigidbodyState();
                RestoreControlRigidbodyState();
                // Also destroy medium object if it exists
                DestroyMediumObject();
                // Clear restoration flag if it was pending
                _needsRestoration = false;
            }
            catch (Exception e)
            {
                SuperController.LogError($"{nameof(SmartDildo)}.{nameof(OnDisable)}: " + e);
            }
        }


        private void InitializeUI()
        {
            _enabledJson = new JSONStorableBool("Enabled", true);
            RegisterBool(_enabledJson);
            CreateToggle(_enabledJson);
            _enabledJson.setCallbackFunction += OnEnableDisableChanged;

            _cuaChoiceJson = new JSONStorableStringChooser("Target Atom", new List<string>(), "", "Target Atom");
            RegisterStringChooser(_cuaChoiceJson);
            CreateFilterablePopup(_cuaChoiceJson);
            _cuaChoiceJson.setCallbackFunction += OnAtomSelected;

            _boneChoiceJson = new JSONStorableStringChooser("Target Bone", new List<string>(), "", "Target Bone");
            RegisterStringChooser(_boneChoiceJson);
            CreateFilterablePopup(_boneChoiceJson);
            _boneChoiceJson.setCallbackFunction += OnBoneSelected;

            _syncPositionXJson = new JSONStorableBool("Sync Position X", true);
            RegisterBool(_syncPositionXJson);
            CreateToggle(_syncPositionXJson);
            _syncPositionXJson.setCallbackFunction += _ => OnSyncOptionsChanged();

            _syncPositionYJson = new JSONStorableBool("Sync Position Y", false);
            RegisterBool(_syncPositionYJson);
            CreateToggle(_syncPositionYJson);
            _syncPositionYJson.setCallbackFunction += _ => OnSyncOptionsChanged();

            _syncPositionZJson = new JSONStorableBool("Sync Position Z", true);
            RegisterBool(_syncPositionZJson);
            CreateToggle(_syncPositionZJson);
            _syncPositionZJson.setCallbackFunction += _ => OnSyncOptionsChanged();


            _pullFactorJson = new JSONStorableFloat("Pull factor", 0.2f, 0f, 5f);
            RegisterFloat(_pullFactorJson);
            CreateSlider(_pullFactorJson);
            _pullFactorJson.setCallbackFunction += _ => OnSyncOptionsChanged();

            _pushFactorJson = new JSONStorableFloat("Push factor", 0.6f, 0f, 5f);
            RegisterFloat(_pushFactorJson);
            CreateSlider(_pushFactorJson);
            _pushFactorJson.setCallbackFunction += _ => OnSyncOptionsChanged();

            _syncRotationJson = new JSONStorableBool("Sync Rotation", true);
            RegisterBool(_syncRotationJson);
            CreateToggle(_syncRotationJson);
            _syncRotationJson.setCallbackFunction += _ => OnSyncOptionsChanged();

            _followMovementJson = new JSONStorableBool("Follow Movement", true);
            RegisterBool(_followMovementJson);

            _syncSpeedJson = new JSONStorableFloat("Sync Speed", 25f, 0.0f, 25.0f);
            RegisterFloat(_syncSpeedJson);
            CreateSlider(_syncSpeedJson);
            _syncSpeedJson.setCallbackFunction += val =>
            {
                if (_hardParentingJson.val) return;
                var dragValue = Mathf.Lerp(10f, 0.1f, val / 25f);
                var rb = _controlRigidbody;
                if (rb == null) return;
                rb.drag = dragValue;
                rb.angularDrag = dragValue;
            };

            _hardParentingJson = new JSONStorableBool("Hard Parenting", false);
            RegisterBool(_hardParentingJson);
            _hardParentingToggle = CreateToggle(_hardParentingJson);
            _hardParentingToggle.label = "Hard Parenting (Instant)";
            _hardParentingJson.setCallbackFunction += OnHardParentingChanged;

            _offsetMultiplierJson = new JSONStorableFloat("Offset Multiplier", 1.0f, 0.1f, 10.0f)
            {
                storeType = JSONStorableParam.StoreType.Full
            };
            RegisterFloat(_offsetMultiplierJson);
            _offsetMultiplierSlider = CreateSlider(_offsetMultiplierJson);
            _offsetMultiplierSlider.label = "Position Offset Multiplier";


            _offsetXJson = new JSONStorableFloat("Offset X", 0f, -1f, 1f);
            RegisterFloat(_offsetXJson);
            CreateSlider(_offsetXJson);

            _offsetYJson = new JSONStorableFloat("Offset Y", -0.23f, -1f, 1f);
            RegisterFloat(_offsetYJson);
            CreateSlider(_offsetYJson);

            _offsetZJson = new JSONStorableFloat("Offset Z", 0f, -1f, 1f);
            RegisterFloat(_offsetZJson);
            CreateSlider(_offsetZJson);

            _offsetRotXJson = new JSONStorableFloat("Rotation X", -90f, -180f, 180f);
            RegisterFloat(_offsetRotXJson);
            CreateSlider(_offsetRotXJson);

            _offsetRotYJson = new JSONStorableFloat("Rotation Y", 0f, -180f, 180f);
            RegisterFloat(_offsetRotYJson);
            CreateSlider(_offsetRotYJson);

            _offsetRotZJson = new JSONStorableFloat("Rotation Z", 0f, -180f, 180f);
            RegisterFloat(_offsetRotZJson);
            CreateSlider(_offsetRotZJson);

            _resetOffsetButton = CreateButton("Reset Offsets");
            _resetOffsetButton.button.onClick.AddListener(() => ResetOffsets());

            CreateSpacer();
            InitializePoseUI();

            _debugModeJson = new JSONStorableBool("Debug Mode", false);
            RegisterBool(_debugModeJson);
            CreateToggle(_debugModeJson, true);

            var delayedDisableButton = CreateButton("Delayed Disable", true);
            delayedDisableButton.button.onClick.AddListener(TriggerDelayedDisable);
            var delayedDisableAction = new JSONStorableAction("DelayedDisable", TriggerDelayedDisable);
            RegisterAction(delayedDisableAction);

            var resetButton = CreateButton("Reset Selection", true);
            resetButton.button.onClick.AddListener(() => ResetSelection());
        }

        private void TriggerDelayedDisable()
        {
            StartCoroutine(DelayedDisableCoroutine());
        }

        private IEnumerator DelayedDisableCoroutine()
        {
            if (_debugModeJson.val) SuperController.LogMessage("Delayed disable triggered. Waiting 1 second.");
            yield return new WaitForSeconds(1.0f);
            if (_enabledJson == null) yield break;
            if (_debugModeJson.val) SuperController.LogMessage("Disabling plugin via delayed button.");
            _enabledJson.val = false;
        }

        private static string GetStateKey(string atomUid, string assetName)
        {
            if (string.IsNullOrEmpty(atomUid)) return null;
            var safeAssetName = string.IsNullOrEmpty(assetName) || assetName.ToLower() == "none" ? "_NO_ASSET_" : assetName;
            return atomUid + "|" + safeAssetName;
        }

        private void SaveCurrentState()
        {
            if (!_cuaAtom || _isBeingDestroyed) return;
            var key = GetStateKey(_cuaAtom.uid, _currentAssetNameForAtom);
            if (key == null) return;

            if (!_assetStateMemory.ContainsKey(key))
            {
                _assetStateMemory[key] = new AssetState();
            }

            var state = _assetStateMemory[key];

            state.HardParenting = _hardParentingJson.val;
            state.BonePath = _lastSelectedBonePath;

            if (_debugModeJson.val) SuperController.LogMessage($"Saved state for key '{key}'");
        }

        private void LoadStateForKey(string key)
        {
            if (key == null || !_assetStateMemory.ContainsKey(key))
            {
                if (_debugModeJson.val) SuperController.LogMessage($"No saved state for key '{key}'. Resetting to defaults.");
                _hardParentingJson.valNoCallback = false;
                _lastSelectedBonePath = null;
                _boneChoiceJson.val = "";
                return;
            }

            var state = _assetStateMemory[key];
            if (_debugModeJson.val) SuperController.LogMessage($"Loading state for key '{key}'");

            _hardParentingJson.valNoCallback = state.HardParenting;
            _lastSelectedBonePath = state.BonePath;
        }

        private void InitializePoseUI()
        {
            CreateSpacer(false);

            _newPoseNameJson = new JSONStorableString("New Pose Name", "Pose 1");
            RegisterString(_newPoseNameJson);
            _newPoseNameTextField = CreateTextField(_newPoseNameJson, true);

            _savePoseButton = CreateButton("Save New Pose", true);
            _savePoseButton.button.onClick.AddListener(() => SaveNewPose());

            var saveNewPoseAction = new JSONStorableAction("SaveNewPose", SaveNewPose);
            RegisterAction(saveNewPoseAction);

            _replacePoseButton = CreateButton("Replace Selected Pose", true);
            _replacePoseButton.button.onClick.AddListener(() => ReplaceSelectedPose());

            var replaceSelectedPoseAction = new JSONStorableAction("ReplaceSelectedPose", ReplaceSelectedPose);
            RegisterAction(replaceSelectedPoseAction);

            _poseChoiceJson = new JSONStorableStringChooser("Saved Poses", new List<string>(), "", "Saved Poses");
            RegisterStringChooser(_poseChoiceJson);
            _poseChoicePopup = CreateScrollablePopup(_poseChoiceJson, true);

            _poseChoiceJson.setCallbackFunction += val =>
            {
                if (_initialized && !string.IsNullOrEmpty(val) && _savedPoses.ContainsKey(val))
                    LoadSelectedPose();
            };

            _loadPoseButton = CreateButton("Load Selected Pose", true);
            _loadPoseButton.button.onClick.AddListener(() => LoadSelectedPose());

            var loadSelectedPoseAction = new JSONStorableAction("LoadSelectedPose", LoadSelectedPose);
            RegisterAction(loadSelectedPoseAction);

            _deletePoseButton = CreateButton("Delete Selected Pose", true);
            _deletePoseButton.button.onClick.AddListener(() => DeleteSelectedPose());

            UpdatePoseUI();
        }

        private void OnEnableDisableChanged(bool isEnabled)
        {
            if (!enabled) return;

            if (_debugModeJson.val) SuperController.LogMessage($"Internal Enabled toggle changed: {isEnabled}");
            if (!isEnabled)
            {
                RestoreRigidbodyState();
                RestoreControlRigidbodyState();
                DestroyMediumObject();
            }
            else
            {
                if (_selectedBone == null) return;
                ApplyCurrentRigidbodyState();
                HandleOffsetToggleChange();
            }
        }

        private void OnSyncOptionsChanged()
        {
            if (!enabled || !_enabledJson.val || _selectedBone == null) return;

            ApplyCurrentRigidbodyState();
        }


        private void OnHardParentingChanged(bool isHardParenting)
        {
            if (!enabled || !_enabledJson.val || _selectedBone == null) return;

            ApplyCurrentRigidbodyState();
        }

        private void ApplyCurrentRigidbodyState()
        {
            if (!enabled || !_enabledJson.val || !_selectedBone || !_cuaAtom || !_cuaAtom.on)
            {
                if (_debugModeJson.val)
                {
                    if (!enabled) SuperController.LogMessage("ApplyState: Plugin natively disabled, skipping RB setup.");
                    else if (!_enabledJson.val) SuperController.LogMessage("ApplyState: Plugin internally disabled, skipping RB setup.");
                    else if (!_selectedBone) SuperController.LogMessage("ApplyState: No bone selected, skipping RB setup.");
                    else if (!_cuaAtom) SuperController.LogMessage("ApplyState: Target atom is null, skipping RB setup.");
                    else if (!_cuaAtom.on) SuperController.LogMessage($"ApplyState: Target atom '{_cuaAtom.uid}' is OFF, skipping RB setup.");
                }

                RestoreRigidbodyState();
                RestoreControlRigidbodyState();
                return;
            }

            var isHard = _hardParentingJson.val;
            var shouldSync = _syncPositionXJson.val || _syncPositionYJson.val || _syncPositionZJson.val || _syncRotationJson.val;

            Transform currentTargetTransform = null;

            if (_cuaAtom)
            {
                currentTargetTransform = _controlPoint;
            }

            RestoreRigidbodyState();

            if (!currentTargetTransform)
            {
                return;
            }

            var currentTargetRb = currentTargetTransform.GetComponent<Rigidbody>();
            var currentHadRb = _cuaAtom ? _hadControlRigidbody : _hadRigidbody;


            if (isHard)
            {
                if (!shouldSync)
                {
                    if (_debugModeJson.val)
                        SuperController.LogMessage($"ApplyState: Hard Parenting - Sync OFF. Restoring original state.");
                    if (_cuaAtom) RestoreControlRigidbodyState();
                    return;
                }

                if (_debugModeJson.val)
                    SuperController.LogMessage($"ApplyState: Hard Parenting - Sync ON. Forcing Kinematic.");
                if (currentTargetRb)
                {
                    if (!currentHadRb)
                    {
                        if (_cuaAtom)
                        {
                            _hadControlRigidbody = true;
                            _wasControlKinematic = currentTargetRb.isKinematic;
                        }

                        if (_debugModeJson.val)
                            SuperController.LogMessage(
                                $"Hard Parenting: Storing original kinematic ({currentTargetRb.isKinematic}) for {currentTargetTransform.name}");
                    }

                    if (!currentTargetRb.isKinematic)
                    {
                        currentTargetRb.isKinematic = true;
                        if (_debugModeJson.val) SuperController.LogMessage($"Hard Parenting: Set RB on {currentTargetTransform.name} to kinematic.");
                    }
                }

                if (_cuaAtom) _controlRigidbody = currentTargetRb;
            }
            else
            {
                if (shouldSync)
                {
                    if (_debugModeJson.val)
                        SuperController.LogMessage($"ApplyState: Soft Parenting - Sync ON. Ensuring Non-Kinematic.");
                    SetupRigidbody(currentTargetTransform, _cuaAtom);
                }
                else
                {
                    if (_debugModeJson.val)
                        SuperController.LogMessage($"ApplyState: Soft Parenting - Sync OFF. Restoring original state.");
                    if (_cuaAtom) RestoreControlRigidbodyState();
                    else RestoreRigidbodyState();
                }
            }
        }


        private void SetupRigidbody(Transform target, bool isControlPoint)
        {
            var rb = target.GetComponent<Rigidbody>();

            bool hadRb;
            bool wasKin;

            if (isControlPoint)
            {
                hadRb = _hadControlRigidbody;
            }
            else
            {
                hadRb = _hadRigidbody;
            }


            if (rb)
            {
                if (!hadRb)
                {
                    if (isControlPoint)
                    {
                        _hadControlRigidbody = true;
                        _wasControlKinematic = rb.isKinematic;
                    }
                    else
                    {
                        _hadRigidbody = true;
                        _wasKinematic = rb.isKinematic;
                    }

                    wasKin = rb.isKinematic;

                    if (_debugModeJson.val) SuperController.LogMessage($"SetupRB (Soft): Found existing RB on {target.name}. Original isKinematic: {wasKin}");
                }

                if (rb.isKinematic)
                {
                    rb.isKinematic = false;
                    if (_debugModeJson.val) SuperController.LogMessage($"SetupRB (Soft): Set existing RB on {target.name} to NON-kinematic.");
                }

                if (isControlPoint)
                {
                    _controlRigidbody = rb;
                    _addedControlRigidbody = false;
                }
                else
                {
                    _boneRigidbody = rb;
                    _addedRigidbody = false;
                }
            }
            else
            {
                rb = target.gameObject.AddComponent<Rigidbody>();
                rb.isKinematic = false;
                rb.useGravity = false;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

                if (isControlPoint)
                {
                    _addedControlRigidbody = true;
                    _hadControlRigidbody = false;
                    _controlRigidbody = rb;
                }
                else
                {
                    _addedRigidbody = true;
                    _hadRigidbody = false;
                    _boneRigidbody = rb;
                }

                if (_debugModeJson.val)
                {
                    SuperController.LogMessage($"SetupRB (Soft): Added NON-kinematic rigidbody to {target.name}");
                }
            }

            var dragValue = Mathf.Lerp(10f, 0.1f, _syncSpeedJson.val / 25f);
            rb.drag = dragValue;
            rb.angularDrag = dragValue;
        }


        private void UpdateUIForAtomType(bool isPerson)
        {
            if (_hardParentingToggle && !_hardParentingToggle.gameObject.activeSelf)
            {
                if (_debugModeJson.val) SuperController.LogMessage($"Setting Hard Parenting UI active: true");
                _hardParentingToggle.gameObject.SetActive(true);
            }

            var shouldShowPoseUI = !isPerson;
            if (_poseUIVisible == shouldShowPoseUI) return;
            if (_debugModeJson.val) SuperController.LogMessage($"Setting Pose UI active: {shouldShowPoseUI}");

            if (_newPoseNameTextField) _newPoseNameTextField.gameObject.SetActive(shouldShowPoseUI);
            if (_savePoseButton) _savePoseButton.gameObject.SetActive(shouldShowPoseUI);
            if (_replacePoseButton) _replacePoseButton.gameObject.SetActive(shouldShowPoseUI);
            if (_poseChoicePopup) _poseChoicePopup.gameObject.SetActive(shouldShowPoseUI);
            if (_loadPoseButton) _loadPoseButton.gameObject.SetActive(shouldShowPoseUI);
            if (_deletePoseButton) _deletePoseButton.gameObject.SetActive(shouldShowPoseUI);

            _poseUIVisible = shouldShowPoseUI;
        }


        private void UpdatePoseUI()
        {
            if (_poseChoiceJson == null || !_loadPoseButton || !_deletePoseButton || !_poseUIVisible)
            {
                return;
            }

            var poseNames = new List<string>(_savedPoses.Keys);
            poseNames.Sort();

            _poseChoiceJson.choices = poseNames;

            if (!string.IsNullOrEmpty(_poseChoiceJson.val) && poseNames.Contains(_poseChoiceJson.val))
            {
                var currentSelection = _poseChoiceJson.val;
                _poseChoiceJson.val = "";
                _poseChoiceJson.val = currentSelection;
            }
            else if (poseNames.Count > 0)
            {
                _poseChoiceJson.val = poseNames[0];
            }
            else
            {
                _poseChoiceJson.val = "";
                _newPoseNameJson.val = "Pose 1";
            }

            var hasPoses = poseNames.Count > 0;
            _loadPoseButton.button.interactable = hasPoses;
            _deletePoseButton.button.interactable = hasPoses;
            if (_replacePoseButton) _replacePoseButton.button.interactable = hasPoses;
        }

        private void SaveCurrentPose()
        {
            if (_cuaAtom == null || (_cuaAtom.type == "Person" && !_poseUIVisible))
            {
                SuperController.LogError("Cannot save pose: No atom selected or operation disabled for Person atom");
                return;
            }

            var poseName = _newPoseNameJson.val.Trim();
            if (string.IsNullOrEmpty(poseName))
            {
                SuperController.LogError("Cannot save pose: Invalid name");
                return;
            }

            var poseData = new JSONClass
            {
                ["atomType"] = _cuaAtom.type
            };
            var containerTransformData = new JSONClass();
            var containerPosArray = new JSONArray();
            containerPosArray.Add(new JSONData(containingAtom.mainController.transform.position.x));
            containerPosArray.Add(new JSONData(containingAtom.mainController.transform.position.y));
            containerPosArray.Add(new JSONData(containingAtom.mainController.transform.position.z));
            containerTransformData["position"] = containerPosArray;

            var containerRotArray = new JSONArray();
            containerRotArray.Add(new JSONData(containingAtom.mainController.transform.rotation.x));
            containerRotArray.Add(new JSONData(containingAtom.mainController.transform.rotation.y));
            containerRotArray.Add(new JSONData(containingAtom.mainController.transform.rotation.z));
            containerRotArray.Add(new JSONData(containingAtom.mainController.transform.rotation.w));
            containerTransformData["rotation"] = containerRotArray;

            poseData["containerTransform"] = containerTransformData;

            if (_cuaAtom != null && _cuaAtom.mainController != null)
            {
                var targetTransformData = new JSONClass();
                var targetPosArray = new JSONArray();
                targetPosArray.Add(new JSONData(_cuaAtom.mainController.transform.position.x));
                targetPosArray.Add(new JSONData(_cuaAtom.mainController.transform.position.y));
                targetPosArray.Add(new JSONData(_cuaAtom.mainController.transform.position.z));
                targetTransformData["position"] = targetPosArray;

                var targetRotArray = new JSONArray();
                targetRotArray.Add(new JSONData(_cuaAtom.mainController.transform.rotation.x));
                targetRotArray.Add(new JSONData(_cuaAtom.mainController.transform.rotation.y));
                targetRotArray.Add(new JSONData(_cuaAtom.mainController.transform.rotation.z));
                targetRotArray.Add(new JSONData(_cuaAtom.mainController.transform.rotation.w));
                targetTransformData["rotation"] = targetRotArray;
                poseData["targetTransform"] = targetTransformData;
            }

            var bonesArray = new JSONArray();
            poseData["bones"] = bonesArray;

            var allBones = GatherAllBones();

            foreach (var pair in allBones)
            {
                var bonePath = pair.Key;
                var boneTransform = pair.Value;

                if (boneTransform == null) continue;
                var boneData = new JSONClass
                {
                    ["path"] = bonePath
                };

                var posArray = new JSONArray();
                posArray.Add(new JSONData(boneTransform.localPosition.x));
                posArray.Add(new JSONData(boneTransform.localPosition.y));
                posArray.Add(new JSONData(boneTransform.localPosition.z));
                boneData["position"] = posArray;

                var rotArray = new JSONArray();
                rotArray.Add(new JSONData(boneTransform.localRotation.x));
                rotArray.Add(new JSONData(boneTransform.localRotation.y));
                rotArray.Add(new JSONData(boneTransform.localRotation.z));
                rotArray.Add(new JSONData(boneTransform.localRotation.w));
                boneData["rotation"] = rotArray;

                var scaleArray = new JSONArray();
                scaleArray.Add(new JSONData(boneTransform.localScale.x));
                scaleArray.Add(new JSONData(boneTransform.localScale.y));
                scaleArray.Add(new JSONData(boneTransform.localScale.z));
                boneData["scale"] = scaleArray;

                bonesArray.Add(boneData);
            }

            _savedPoses[poseName] = poseData;
            UpdatePoseUI();
            _poseChoiceJson.val = poseName;
            IncrementPoseName();

            if (_debugModeJson.val)
            {
                SuperController.LogMessage($"Saved pose '{poseName}' with {bonesArray.Count} bones, including container transforms");
            }
        }

        private void SaveNewPose()
        {
            if (_cuaAtom == null || (_cuaAtom.type == "Person" && !_poseUIVisible))
            {
                SuperController.LogError("Cannot save pose: No atom selected or operation disabled for Person atom");
                return;
            }

            var poseName = _newPoseNameJson.val.Trim();
            if (string.IsNullOrEmpty(poseName))
            {
                SuperController.LogError("Cannot save pose: Invalid name");
                return;
            }

            SaveCurrentPose();
        }

        private void ReplaceSelectedPose()
        {
            if (_cuaAtom == null || (_cuaAtom.type == "Person" && !_poseUIVisible))
            {
                SuperController.LogError("Cannot replace pose: No atom selected or operation disabled for Person atom");
                return;
            }

            var poseName = _poseChoiceJson.val;
            if (string.IsNullOrEmpty(poseName) || !_savedPoses.ContainsKey(poseName))
            {
                SuperController.LogError("Cannot replace pose: No pose selected");
                return;
            }

            _savedPoses[poseName] = CreateCurrentPoseData();

            if (_debugModeJson.val)
            {
                SuperController.LogMessage($"Replaced pose '{poseName}'");
            }
        }

        private JSONClass CreateCurrentPoseData()
        {
            var poseData = new JSONClass
            {
                ["atomType"] = _cuaAtom.type
            };

            var containerTransformData = new JSONClass();
            var containerPosArray = new JSONArray();
            containerPosArray.Add(new JSONData(containingAtom.mainController.transform.position.x));
            containerPosArray.Add(new JSONData(containingAtom.mainController.transform.position.y));
            containerPosArray.Add(new JSONData(containingAtom.mainController.transform.position.z));
            containerTransformData["position"] = containerPosArray;

            var containerRotArray = new JSONArray();
            containerRotArray.Add(new JSONData(containingAtom.mainController.transform.rotation.x));
            containerRotArray.Add(new JSONData(containingAtom.mainController.transform.rotation.y));
            containerRotArray.Add(new JSONData(containingAtom.mainController.transform.rotation.z));
            containerRotArray.Add(new JSONData(containingAtom.mainController.transform.rotation.w));
            containerTransformData["rotation"] = containerRotArray;

            poseData["containerTransform"] = containerTransformData;

            if (_cuaAtom != null && _cuaAtom.mainController != null)
            {
                var targetTransformData = new JSONClass();
                var targetPosArray = new JSONArray();
                targetPosArray.Add(new JSONData(_cuaAtom.mainController.transform.position.x));
                targetPosArray.Add(new JSONData(_cuaAtom.mainController.transform.position.y));
                targetPosArray.Add(new JSONData(_cuaAtom.mainController.transform.position.z));
                targetTransformData["position"] = targetPosArray;

                var targetRotArray = new JSONArray();
                targetRotArray.Add(new JSONData(_cuaAtom.mainController.transform.rotation.x));
                targetRotArray.Add(new JSONData(_cuaAtom.mainController.transform.rotation.y));
                targetRotArray.Add(new JSONData(_cuaAtom.mainController.transform.rotation.z));
                targetRotArray.Add(new JSONData(_cuaAtom.mainController.transform.rotation.w));
                targetTransformData["rotation"] = targetRotArray;

                poseData["targetTransform"] = targetTransformData;
            }

            var bonesArray = new JSONArray();
            var allBones = GatherAllBones();

            foreach (var pair in allBones)
            {
                var bonePath = pair.Key;
                var boneTransform = pair.Value;

                if (boneTransform != null)
                {
                    var boneData = new JSONClass
                    {
                        ["path"] = bonePath
                    };

                    var posArray = new JSONArray();
                    posArray.Add(new JSONData(boneTransform.localPosition.x));
                    posArray.Add(new JSONData(boneTransform.localPosition.y));
                    posArray.Add(new JSONData(boneTransform.localPosition.z));
                    boneData["position"] = posArray;

                    var rotArray = new JSONArray();
                    rotArray.Add(new JSONData(boneTransform.localRotation.x));
                    rotArray.Add(new JSONData(boneTransform.localRotation.y));
                    rotArray.Add(new JSONData(boneTransform.localRotation.z));
                    rotArray.Add(new JSONData(boneTransform.localRotation.w));
                    boneData["rotation"] = rotArray;

                    var scaleArray = new JSONArray();
                    scaleArray.Add(new JSONData(boneTransform.localScale.x));
                    scaleArray.Add(new JSONData(boneTransform.localScale.y));
                    scaleArray.Add(new JSONData(boneTransform.localScale.z));
                    boneData["scale"] = scaleArray;

                    bonesArray.Add(boneData);
                }
            }

            poseData["bones"] = bonesArray;

            return poseData;
        }

        private void IncrementPoseName()
        {
            if (_savedPoses.Count == 0)
            {
                _newPoseNameJson.val = "Pose 1";
                return;
            }

            var highestNumber = 0;
            var poseKeys = new string[_savedPoses.Count];
            _savedPoses.Keys.CopyTo(poseKeys, 0);

            for (var i = 0; i < poseKeys.Length; i++)
            {
                var poseName = poseKeys[i];
                int number;
                if (int.TryParse(poseName.Replace("Pose ", ""), out number))
                {
                    highestNumber = Mathf.Max(highestNumber, number);
                }
            }

            _newPoseNameJson.val = "Pose " + (highestNumber + 1);
        }

        private Dictionary<string, Transform> GatherAllBones()
        {
            var bones = new Dictionary<string, Transform>();

            if (!_cuaAtom || !_cuaRoot)
                return bones;

            if (_cuaAtom.type == "Person")
            {
                var physics = _cuaAtom.GetStorableByID("PhysicsModel");
                if (!physics) return bones;
                var physicsTransform = physics.transform;
                var allBones = new List<Transform>();

                RecursiveGatherBones(physicsTransform, allBones);

                foreach (var bone in allBones)
                {
                    if (bone)
                    {
                        bones[bone.name] = bone;
                    }
                }
            }
            else
            {
                var frontier = new Queue<Transform>();
                frontier.Enqueue(_cuaRoot);

                while (frontier.Count > 0)
                {
                    var node = frontier.Dequeue();
                    if (!node)
                        continue;

                    var fullPath = GetPathToRoot(node, _cuaRoot);
                    if (!string.IsNullOrEmpty(fullPath))
                    {
                        bones[fullPath] = node;
                    }

                    for (var i = 0; i < node.childCount; i++)
                    {
                        frontier.Enqueue(node.GetChild(i));
                    }
                }
            }

            return bones;
        }

        private void LoadSelectedPose()
        {
            var poseName = _poseChoiceJson.val;
            if (string.IsNullOrEmpty(poseName) || !_savedPoses.ContainsKey(poseName))
            {
                SuperController.LogError("Cannot load pose: Invalid pose selected");
                return;
            }

            if (_cuaAtom == null || (_cuaAtom.type == "Person" && !_poseUIVisible))
            {
                SuperController.LogError("Cannot load pose: No atom selected or operation disabled for Person atom");
                return;
            }

            var poseData = _savedPoses[poseName];
            string savedAtomType = poseData["atomType"];
            var isCompatible = (savedAtomType == _cuaAtom.type) ||
                               (savedAtomType == "CustomUnityAsset" && HasCustomAsset(_cuaAtom)) ||
                               (_cuaAtom.type == "CustomUnityAsset" && savedAtomType == "CUA");

            if (!isCompatible)
            {
                SuperController.LogError($"Cannot load pose: Pose was saved for '{savedAtomType}' but current atom is '{_cuaAtom.type}'");
                return;
            }

            if (poseData.HasKey("containerTransform"))
            {
                var containerTransformData = poseData["containerTransform"].AsObject;
                if (containerTransformData != null)
                {
                    try
                    {
                        if (containerTransformData.HasKey("position"))
                        {
                            var posArray = containerTransformData["position"].AsArray;
                            if (posArray != null && posArray.Count >= 3)
                            {
                                var position = new Vector3(posArray[0].AsFloat, posArray[1].AsFloat, posArray[2].AsFloat);
                                containingAtom.mainController.transform.position = position;
                            }
                        }

                        if (containerTransformData.HasKey("rotation"))
                        {
                            var rotArray = containerTransformData["rotation"].AsArray;
                            if (rotArray != null && rotArray.Count >= 4)
                            {
                                Quaternion rotation = new Quaternion(rotArray[0].AsFloat, rotArray[1].AsFloat, rotArray[2].AsFloat, rotArray[3].AsFloat);
                                containingAtom.mainController.transform.rotation = rotation;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        SuperController.LogError($"Error restoring container transform: {e.Message}");
                    }
                }
            }

            if (poseData.HasKey("targetTransform") && _cuaAtom != null && _cuaAtom.mainController != null)
            {
                var targetTransformData = poseData["targetTransform"].AsObject;
                if (targetTransformData != null)
                {
                    try
                    {
                        if (targetTransformData.HasKey("position"))
                        {
                            var posArray = targetTransformData["position"].AsArray;
                            if (posArray != null && posArray.Count >= 3)
                            {
                                var position = new Vector3(posArray[0].AsFloat, posArray[1].AsFloat, posArray[2].AsFloat);
                                _cuaAtom.mainController.transform.position = position;
                            }
                        }

                        if (targetTransformData.HasKey("rotation"))
                        {
                            var rotArray = targetTransformData["rotation"].AsArray;
                            if (rotArray != null && rotArray.Count >= 4)
                            {
                                Quaternion rotation = new Quaternion(rotArray[0].AsFloat, rotArray[1].AsFloat, rotArray[2].AsFloat, rotArray[3].AsFloat);
                                _cuaAtom.mainController.transform.rotation = rotation;
                            }
                        }

                        if (_debugModeJson.val)
                        {
                            SuperController.LogMessage("Restored target atom's control transform");
                        }
                    }
                    catch (Exception e)
                    {
                        SuperController.LogError($"Error restoring target atom transform: {e.Message}");
                    }
                }
            }

            var bonesArray = poseData["bones"].AsArray;
            if (bonesArray == null || bonesArray.Count == 0)
            {
                SuperController.LogError("Cannot load pose: Pose data is empty");
                return;
            }

            var allBones = GatherAllBones();
            var updatedBones = 0;

            foreach (JSONNode boneNode in bonesArray)
            {
                var boneData = boneNode.AsObject;
                if (boneData == null) continue;

                string bonePath = boneData["path"];
                if (string.IsNullOrEmpty(bonePath)) continue;

                Transform bone = null;
                if (allBones.ContainsKey(bonePath))
                {
                    bone = allBones[bonePath];
                }
                else if (_cuaAtom.type == "Person")
                {
                    foreach (KeyValuePair<string, Transform> pair in allBones)
                    {
                        var boneName = pair.Key;
                        if (boneName != bonePath) continue;
                        bone = pair.Value;
                        break;
                    }
                }

                if (bone == null) continue;

                try
                {
                    var posArray = boneData["position"].AsArray;
                    if (posArray != null && posArray.Count >= 3)
                    {
                        bone.localPosition = new Vector3(posArray[0].AsFloat, posArray[1].AsFloat, posArray[2].AsFloat);
                    }

                    var rotArray = boneData["rotation"].AsArray;
                    if (rotArray != null && rotArray.Count >= 4)
                    {
                        bone.localRotation = new Quaternion(rotArray[0].AsFloat, rotArray[1].AsFloat, rotArray[2].AsFloat, rotArray[3].AsFloat);
                    }

                    var scaleArray = boneData["scale"].AsArray;
                    if (scaleArray != null && scaleArray.Count >= 3)
                    {
                        bone.localScale = new Vector3(scaleArray[0].AsFloat, scaleArray[1].AsFloat, scaleArray[2].AsFloat);
                    }

                    updatedBones++;
                }
                catch (Exception e)
                {
                    if (_debugModeJson.val)
                    {
                        SuperController.LogError($"Error applying transforms to bone {bonePath}: {e}");
                    }
                }
            }

            StartCoroutine(LockBonesTemporarily());

            if (_debugModeJson.val)
            {
                SuperController.LogMessage($"Loaded pose '{poseName}': Updated {updatedBones} of {bonesArray.Count} bones with container transforms");
            }
        }

        private bool _isApplyingPose;

        private IEnumerator LockBonesTemporarily()
        {
            if (_isApplyingPose) yield break;

            try
            {
                _isApplyingPose = true;
                var bodiesToLock = new List<Rigidbody>();
                var originalKinematicStates = new List<bool>();

                var allBones = GatherAllBones();
                var boneKeys = new string[allBones.Count];
                allBones.Keys.CopyTo(boneKeys, 0);

                for (var i = 0; i < boneKeys.Length; i++)
                {
                    var bone = allBones[boneKeys[i]];
                    var rb = bone.GetComponent<Rigidbody>();
                    if (!rb) continue;
                    bodiesToLock.Add(rb);
                    originalKinematicStates.Add(rb.isKinematic);
                    rb.isKinematic = true;
                }

                yield return new WaitForSeconds(0.25f);

                for (var i = 0; i < bodiesToLock.Count; i++)
                {
                    if (bodiesToLock[i])
                    {
                        bodiesToLock[i].isKinematic = originalKinematicStates[i];
                    }
                }
            }
            finally
            {
                _isApplyingPose = false;
            }
        }

        private void DeleteSelectedPose()
        {
            var poseName = _poseChoiceJson.val;
            if (string.IsNullOrEmpty(poseName) || !_savedPoses.ContainsKey(poseName) || !_poseUIVisible)
            {
                return;
            }

            _savedPoses.Remove(poseName);
            UpdatePoseUI();
            IncrementPoseName();

            if (_debugModeJson.val)
            {
                SuperController.LogMessage($"Deleted pose '{poseName}'");
            }
        }

        private void ResetOffsets()
        {
            _offsetXJson.val = 0f;
            _offsetYJson.val = 0f;
            _offsetZJson.val = 0f;
            _offsetRotXJson.val = 0f;
            _offsetRotYJson.val = 0f;
            _offsetRotZJson.val = 0f;
            if (_mediumTransform != null)
            {
                ApplyOffsetsToMediumObject();
            }
        }

        private void CreateMediumObject()
        {
            if (!_mediumObject && _selectedBone)
            {
                _mediumObject = new GameObject("SmartDildoOffset");
                _mediumTransform = _mediumObject.transform;
                _mediumTransform.position = _selectedBone.position;
                _mediumTransform.rotation = _selectedBone.rotation;
                ApplyOffsetsToMediumObject();
                if (_debugModeJson.val) SuperController.LogMessage("Created medium object for offset");
            }
        }

        private void ApplyOffsetsToMediumObject()
        {
            if (!_mediumTransform || !_selectedBone) return;

            var posMultiplier = _offsetMultiplierJson.val;

            _mediumTransform.position = _selectedBone.position;
            _mediumTransform.rotation = _selectedBone.rotation;

            _mediumTransform.Translate(new Vector3(_offsetXJson.val * posMultiplier, _offsetYJson.val * posMultiplier, _offsetZJson.val * posMultiplier),
                Space.Self);

            Vector3 currentRotation = new Vector3(_offsetRotXJson.val, _offsetRotYJson.val, _offsetRotZJson.val);
            Quaternion offsetRotation = Quaternion.Euler(currentRotation);
            _mediumTransform.rotation = _mediumTransform.rotation * offsetRotation;
        }


        private void DestroyMediumObject()
        {
            if (_mediumObject)
            {
                Destroy(_mediumObject);
                _mediumObject = null;
                _mediumTransform = null;
                if (_debugModeJson.val) SuperController.LogMessage("Destroyed medium object");
            }
        }

        private void ResetSelection()
        {
            RestoreRigidbodyState();
            RestoreControlRigidbodyState();
            DestroyMediumObject();

            _assetStateMemory.Clear();

            _selectedBone = null;
            _boneChoiceJson.val = "";

            PopulateAtomList();
        }

        private void PopulateAtomList()
        {
            try
            {
                var previouslySelectedAtom = _cuaChoiceJson.val;
                var hadPreviousSelection = !string.IsNullOrEmpty(previouslySelectedAtom);

                _cuaAtomNames.Clear();
                var allAtoms = SuperController.singleton.GetAtoms();
                var allFemales = new List<Atom>();

                if (_debugModeJson.val) SuperController.LogMessage("Total atoms in scene: " + allAtoms.Count);

                foreach (var atom in allAtoms)
                {
                    if (atom.type == "Person")
                    {
                        var geometry = atom.GetStorableByID("geometry") as DAZCharacterSelector;
                        if (!geometry) continue;

                        if (geometry.gender == DAZCharacterSelector.Gender.Female)
                        {
                            allFemales.Add(atom);
                        }
                    }

                    _cuaAtomNames.Add(atom.uid);
                    if (_debugModeJson.val) SuperController.LogMessage("Added atom: " + atom.uid + " (Type: " + atom.type + ")");
                }

                _cuaAtomNames.Sort();
                _cuaChoiceJson.choices = new List<string>(_cuaAtomNames);
                var canRestoreSelection = hadPreviousSelection && _cuaAtomNames.Contains(previouslySelectedAtom);

                if (!canRestoreSelection && _cuaAtom && _cuaAtomNames.Contains(_cuaAtom.uid))
                {
                    previouslySelectedAtom = _cuaAtom.uid;
                    canRestoreSelection = true;
                    if (_debugModeJson.val) SuperController.LogMessage("Using current cuaAtom for restoration: " + _cuaAtom.uid);
                }

                if (canRestoreSelection)
                {
                    if (_debugModeJson.val) SuperController.LogMessage("Restoring atom selection: " + previouslySelectedAtom);

                    if (_switchingAtoms)
                    {
                        _cuaChoiceJson.val = previouslySelectedAtom;
                    }
                    else
                    {
                        var wasSwitchingAtoms = _switchingAtoms;
                        _switchingAtoms = false;
                        _cuaChoiceJson.val = previouslySelectedAtom;
                        _switchingAtoms = wasSwitchingAtoms;
                    }

                    if (_cuaAtom && _cuaAtom.uid != previouslySelectedAtom)
                    {
                        _cuaAtom = SuperController.singleton.GetAtomByUid(previouslySelectedAtom);
                    }
                }
                else
                {
                    if (hadPreviousSelection && _debugModeJson.val)
                        SuperController.LogMessage("Previously selected atom is no longer available: " + previouslySelectedAtom);
                    _cuaChoiceJson.val = "";
                    _cuaAtom = null;
                    UpdateUIForAtomType(false);
                }

                if (allFemales.Count == 1)
                {
                    var female = allFemales[0];
                    _cuaChoiceJson.val = female.uid;
                    OnAtomSelected(female.uid);
                }

                if (_debugModeJson.val) SuperController.LogMessage("Found " + _cuaAtomNames.Count + " atoms");
            }
            catch (Exception e)
            {
                SuperController.LogError("Exception caught in PopulateAtomList: " + e);
            }
        }

        private bool HasCustomAsset(Atom atom)
        {
            try
            {
                if (!atom || !atom.reParentObject) return false;
                _loader = atom.reParentObject.GetComponentInChildren<CustomUnityAssetLoader>(true);
                return _loader;
            }
            catch
            {
                return false;
            }
        }

        private void OnAssetNameChanged(string newAssetName)
        {
            if (_isBeingDestroyed || !this.enabled) return;

            SaveCurrentState();
            _currentAssetNameForAtom = newAssetName;

            var newKey = GetStateKey(_cuaAtom.uid, newAssetName);
            LoadStateForKey(newKey);

            if (_debugModeJson.val) SuperController.LogMessage($"Asset name changed to '{newAssetName}'. Re-initializing bone selection.");

            RestoreRigidbodyState();
            RestoreControlRigidbodyState();
            DestroyMediumObject();

            _selectedBone = null;

            _boneChoiceJson.choices = new List<string>();
            _boneChoiceJson.val = "";
            _boneDisplayToPath.Clear();
            _bonePathToDisplay.Clear();

            StartCoroutine(ReInitializeAfterAssetChange());
        }

        private IEnumerator ReInitializeAfterAssetChange()
        {
            yield return new WaitForSeconds(0.10f);
            if (_cuaAtom)
            {
                if (_debugModeJson.val) SuperController.LogMessage("Populating bone list after asset change.");
                FindAtomRoot();
                PopulateBoneList();

                if (!string.IsNullOrEmpty(_lastSelectedBonePath))
                {
                    if (_bonePathToDisplay.ContainsKey(_lastSelectedBonePath))
                    {
                        var displayName = _bonePathToDisplay[_lastSelectedBonePath];
                        if (_boneChoiceJson.choices.Contains(displayName))
                        {
                            _boneChoiceJson.val = displayName;
                        }
                    }
                    else
                    {
                        _lastSelectedBonePath = null;
                    }
                }
            }
        }

        private void OnAtomSelected(string atomUid)
        {
            try
            {
                _switchingAtoms = true;

                SaveCurrentState();

                if (_cuaAtom)
                {
                    if (_assetNameChooser != null)
                    {
                        _assetNameChooser.setCallbackFunction -= OnAssetNameChanged;
                        _assetNameChooser = null;
                    }
                }

                RestoreRigidbodyState();
                RestoreControlRigidbodyState();

                _selectedBone = null;
                bool isPerson = false;

                if (string.IsNullOrEmpty(atomUid))
                {
                    _cuaAtom = null;
                    _currentAssetNameForAtom = null;
                }
                else
                {
                    _cuaAtom = SuperController.singleton.GetAtomByUid(atomUid);
                    if (!_cuaAtom)
                    {
                        if (_debugModeJson.val) SuperController.LogError("Atom not found: " + atomUid);
                        _currentAssetNameForAtom = null;
                    }
                    else
                    {
                        isPerson = (_cuaAtom.type == "Person");
                        FindAtomRoot();

                        var assetLoader = _cuaAtom.GetComponentInChildren<CustomUnityAssetLoader>(true);
                        if (assetLoader)
                        {
                            _assetNameChooser = assetLoader.GetStringChooserJSONParam("assetName");
                            if (_assetNameChooser != null)
                            {
                                _currentAssetNameForAtom = _assetNameChooser.val;
                                _assetNameChooser.setCallbackFunction += OnAssetNameChanged;
                            }
                            else
                            {
                                _currentAssetNameForAtom = null;
                            }
                        }
                        else
                        {
                            _currentAssetNameForAtom = null;
                        }
                    }
                }

                var key = GetStateKey(_cuaAtom ? _cuaAtom.uid : null, _currentAssetNameForAtom);
                LoadStateForKey(key);

                UpdateUIForAtomType(isPerson);
                PopulateBoneList();

                if (!string.IsNullOrEmpty(_lastSelectedBonePath))
                {
                    if (_bonePathToDisplay.ContainsKey(_lastSelectedBonePath))
                    {
                        var displayName = _bonePathToDisplay[_lastSelectedBonePath];
                        if (_boneChoiceJson.choices.Contains(displayName))
                        {
                            _boneChoiceJson.val = displayName;
                        }
                        else
                        {
                            _boneChoiceJson.val = "";
                            _lastSelectedBonePath = null;
                        }
                    }
                    else
                    {
                        _boneChoiceJson.val = "";
                        _lastSelectedBonePath = null;
                    }
                }
                else
                {
                    _boneChoiceJson.val = "";
                }

                ApplyCurrentRigidbodyState();
                HandleOffsetToggleChange();

                _switchingAtoms = false;
            }
            catch (Exception e)
            {
                _switchingAtoms = false;
                SuperController.LogError("Exception caught in OnAtomSelected: " + e);
                _cuaAtom = null;
                UpdateUIForAtomType(false);
            }
        }


        private void FindAtomRoot()
        {
            _cuaRoot = null;
            if (!_cuaAtom) return;

            if (_cuaAtom.type == "CustomUnityAsset" || HasCustomAsset(_cuaAtom))
            {
                var asset = _cuaAtom.GetStorableByID("asset");
                if (asset)
                {
                    _cuaRoot = asset.transform;
                }

                if (!_cuaRoot)
                {
                    Transform rescaleObject = _cuaAtom.reParentObject?.Find("object/rescaleObject");
                    if (rescaleObject)
                    {
                        _cuaRoot = rescaleObject;
                    }
                }
            }
            else if (_cuaAtom.type == "Person")
            {
                _cuaRoot = _cuaAtom.transform.Find("rescaleObject");

                if (_debugModeJson.val)
                {
                    if (_cuaRoot) SuperController.LogMessage("Found Person rescaleObject");
                }
            }
            else
            {
                _cuaRoot = _cuaAtom.reParentObject;
            }

            if (!_cuaRoot)
            {
                _cuaRoot = _cuaAtom.reParentObject;
                if (_debugModeJson.val && _cuaRoot) SuperController.LogMessage("Using fallback reParentObject for root");
            }

            if (_debugModeJson.val)
            {
                SuperController.LogMessage("Atom Root found: " + (_cuaRoot ? _cuaRoot.name : "NULL") + " for atom type: " + _cuaAtom?.type);
            }
        }

        private void PopulateBoneList()
        {
            var boneNames = new List<string>();
            Transform pelvis = null;
            _boneDisplayToPath.Clear();
            _bonePathToDisplay.Clear();

            try
            {
                if (!_cuaAtom || !_cuaRoot)
                {
                    _boneChoiceJson.choices = boneNames;
                    return;
                }

                var assetLoader = _cuaAtom.GetComponentInChildren<CustomUnityAssetLoader>(true);
                if (assetLoader)
                {
                    var assetNameChooser = assetLoader.GetStringChooserJSONParam("assetName");
                    if (assetNameChooser != null && (string.IsNullOrEmpty(assetNameChooser.val) || assetNameChooser.val.ToLower() == "none"))
                    {
                        _boneChoiceJson.choices = boneNames; // Return empty list if no asset is selected
                        return;
                    }
                }

                if (_cuaAtom.type == "Person")
                {
                    try
                    {
                        var physics = _cuaAtom.GetStorableByID("PhysicsModel");
                        if (physics)
                        {
                            var physicsTransform = physics.transform;
                            var allBones = new List<Transform>();
                            RecursiveGatherBones(physicsTransform, allBones);

                            foreach (Transform bone in allBones)
                            {
                                if (!bone) continue;

                                var displayName = bone.name;
                                var uniqueDisplayName = displayName;
                                var counter = 1;
                                while (_boneDisplayToPath.ContainsKey(uniqueDisplayName))
                                {
                                    uniqueDisplayName = displayName + " [" + counter + "]";
                                    counter++;
                                }

                                var path = bone.name;
                                _boneDisplayToPath[uniqueDisplayName] = path;
                                _bonePathToDisplay[path] = uniqueDisplayName;
                                if (!boneNames.Contains(uniqueDisplayName))
                                {
                                    boneNames.Add(uniqueDisplayName);
                                    if (uniqueDisplayName.Contains("pelvis"))
                                    {
                                        pelvis = bone;
                                    }
                                }
                            }

                            if (_debugModeJson.val) SuperController.LogMessage("Found " + allBones.Count + " bones in Person atom");
                        }
                        else if (_debugModeJson.val)
                        {
                            SuperController.LogError("Could not find PhysicsModel in Person atom");
                        }
                    }
                    catch (Exception e)
                    {
                        SuperController.LogError("Exception finding Person bones: " + e);
                    }
                }
                else
                {
                    var frontier = new Queue<Transform>();
                    frontier.Enqueue(_cuaRoot);

                    var index = 0;
                    while (frontier.Count > 0)
                    {
                        var node = frontier.Dequeue();
                        if (!node) break;

                        var fullPath = GetPathToRoot(node, _cuaRoot);

                        if (!string.IsNullOrEmpty(fullPath))
                        {
                            var displayName = node.name;
                            var uniqueDisplayName = displayName;
                            var counter = 1;
                            while (_boneDisplayToPath.ContainsKey(uniqueDisplayName))
                            {
                                uniqueDisplayName = displayName + " [" + counter + "]";
                                counter++;
                            }

                            _boneDisplayToPath[uniqueDisplayName] = fullPath;
                            _bonePathToDisplay[fullPath] = uniqueDisplayName;
                            if (!boneNames.Contains(uniqueDisplayName)) boneNames.Add(uniqueDisplayName);
                        }

                        for (var i = 0; i < node.childCount; i++)
                        {
                            var child = node.GetChild(i);
                            frontier.Enqueue(child);
                        }

                        index++;
                        if (index <= 500) continue;
                        if (_debugModeJson.val) SuperController.LogMessage("Reached bone limit (500), stopping search.");
                        break;
                    }
                }

                boneNames.Sort();
                _boneChoiceJson.choices = boneNames;
                if (pelvis)
                {
                    _boneChoiceJson.val = pelvis.name;
                    OnBoneSelected(pelvis.name);
                }

                if (_debugModeJson.val) SuperController.LogMessage("Found " + boneNames.Count + " bones in atom");
            }
            catch (Exception e)
            {
                SuperController.LogError("Exception caught in PopulateBoneList: " + e);
                _boneChoiceJson.choices = boneNames;
            }
        }

        private void RecursiveGatherBones(Transform parent, List<Transform> bonesList)
        {
            if (IsPossiblyABone(parent)) bonesList.Add(parent);
            for (var i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                RecursiveGatherBones(child, bonesList);
            }
        }

        private bool IsPossiblyABone(Transform t)
        {
            if (!t) return false;
            if (string.IsNullOrEmpty(t.name)) return false;
            if (t.name.ToLower().Contains("collider")) return false;
            return !t.GetComponent<MeshRenderer>();
        }

        private string GetPathToRoot(Transform t, Transform root)
        {
            if (!t || !root) return "";
            if (t == root) return "";

            if (!t.parent || t.parent == root)
            {
                return t.name;
            }

            var parentPath = GetPathToRoot(t.parent, root);
            return string.IsNullOrEmpty(parentPath) ? t.name : parentPath + "/" + t.name;
        }

        private void RestoreControlRigidbodyState()
        {
            if (!_controlPoint)
            {
                _controlRigidbody = null;
                _addedControlRigidbody = false;
                _hadControlRigidbody = false;
                return;
            }

            var currentRb = _controlPoint.GetComponent<Rigidbody>();

            if (_addedControlRigidbody && currentRb && currentRb == _controlRigidbody)
            {
                if (_debugModeJson.val) SuperController.LogMessage($"RestoreControl: Removing added RB from {_controlPoint.name}");
                DestroyImmediate(currentRb);
            }
            else if (_hadControlRigidbody && currentRb)
            {
                if (currentRb.isKinematic != _wasControlKinematic)
                {
                    if (_debugModeJson.val)
                        SuperController.LogMessage($"RestoreControl: Restoring original kinematic state ({_wasControlKinematic}) for {_controlPoint.name}");
                    currentRb.isKinematic = _wasControlKinematic;
                }
            }

            _controlRigidbody = null;
            _addedControlRigidbody = false;
            _hadControlRigidbody = false;
        }

        private void OnBoneSelected(string displayName)
        {
            try
            {
                if (_switchingAtoms && _selectedBone)
                {
                    if (_debugModeJson.val) SuperController.LogMessage("Skipping bone setup during atom switch");
                    return;
                }

                RestoreRigidbodyState();
                RestoreControlRigidbodyState();
                DestroyMediumObject();

                if (string.IsNullOrEmpty(displayName) || _cuaAtom == null || _cuaRoot == null)
                {
                    _selectedBone = null;
                    _lastSelectedBonePath = null;
                    return;
                }

                if (!_boneDisplayToPath.ContainsKey(displayName))
                {
                    if (_debugModeJson.val) SuperController.LogError("Bone display name not found in mapping: " + displayName);
                    _selectedBone = null;
                    _lastSelectedBonePath = null;
                    return;
                }

                var fullPath = _boneDisplayToPath[displayName];
                _lastSelectedBonePath = fullPath;

                _selectedBone = FindBoneByPath(fullPath);

                if (!_selectedBone)
                {
                    if (_debugModeJson.val) SuperController.LogError("Bone not found: " + fullPath);
                    _lastSelectedBonePath = null;
                    return;
                }

                ApplyCurrentRigidbodyState();
                CreateMediumObject();

                if (_debugModeJson.val) SuperController.LogMessage("Selected bone: " + fullPath);
            }
            catch (Exception e)
            {
                SuperController.LogError("Exception caught in OnBoneSelected: " + e);
                _selectedBone = null;
                _lastSelectedBonePath = null;
                RestoreRigidbodyState();
                RestoreControlRigidbodyState();
            }
        }

        private Transform FindBoneByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            if (_cuaAtom && _cuaAtom.type == "Person")
            {
                try
                {
                    var boneName = path;
                    var physics = _cuaAtom.GetStorableByID("PhysicsModel");
                    if (physics)
                    {
                        var physicsTransform = physics.transform;
                        var allBones = new List<Transform>();
                        RecursiveGatherBones(physicsTransform, allBones);

                        foreach (var bone in allBones)
                        {
                            if (!bone || bone.name != boneName) continue;
                            if (_debugModeJson.val) SuperController.LogMessage("Found Person bone: " + bone.name);
                            return bone;
                        }

                        if (_debugModeJson.val) SuperController.LogMessage($"Person bone '{boneName}' not found in PhysicsModel hierarchy.");
                    }
                    else
                    {
                        if (_debugModeJson.val) SuperController.LogMessage("PhysicsModel not found for Person atom.");
                    }
                }
                catch (Exception e)
                {
                    SuperController.LogError("Exception finding Person bone: " + e);
                }

                return null;
            }

            if (!_cuaRoot) return null;
            var pathComponents = path.Split('/');
            var current = _cuaRoot;

            foreach (var component in pathComponents)
            {
                if (string.IsNullOrEmpty(component)) continue;
                var found = current.Find(component);
                if (!found)
                {
                    if (_debugModeJson.val)
                        SuperController.LogMessage($"Bone path component '{component}' not found under '{current.name}'. Full path: {path}");
                    return null;
                }

                current = found;
            }

            return current;
        }

        private void RestoreRigidbodyState()
        {
            if (!_selectedBone)
            {
                _boneRigidbody = null;
                _addedRigidbody = false;
                _hadRigidbody = false;
                return;
            }

            var currentRb = _selectedBone.GetComponent<Rigidbody>();

            if (_addedRigidbody && currentRb && currentRb == _boneRigidbody)
            {
                if (_debugModeJson.val) SuperController.LogMessage($"RestoreBone: Removing added RB from {_selectedBone.name}");
                DestroyImmediate(currentRb);
            }
            else if (_hadRigidbody && currentRb)
            {
                if (currentRb.isKinematic != _wasKinematic)
                {
                    if (_debugModeJson.val)
                        SuperController.LogMessage($"RestoreBone: Restoring original kinematic state ({_wasKinematic}) for {_selectedBone.name}");
                    currentRb.isKinematic = _wasKinematic;
                }
            }

            _boneRigidbody = null;
            _addedRigidbody = false;
            _hadRigidbody = false;
        }

        private void OnAtomAdded(Atom atom)
        {
            if (_debugModeJson.val) SuperController.LogMessage("Atom added, updating list: " + atom.uid);
            var currentSelectedBone = _selectedBone;
            var currentBonePath = _lastSelectedBonePath;
            var currentAtomUid = _cuaAtom?.uid;

            PopulateAtomList();

            if (_cuaAtom == null || _cuaAtom.uid != currentAtomUid) return;
            if (_selectedBone != null && currentSelectedBone == _selectedBone)
            {
                if (_debugModeJson.val) SuperController.LogMessage("Maintaining existing bone connection after atom added");
            }
            else if (!string.IsNullOrEmpty(currentBonePath))
            {
                if (_debugModeJson.val) SuperController.LogMessage("Attempting to restore bone connection after atom added");
                _needsReconnection = true;
                _lastSelectedBonePath = currentBonePath;
            }
        }

        private void OnAtomRemoved(Atom atom)
        {
            if (_debugModeJson.val) SuperController.LogMessage("Atom removed: " + atom.uid);

            var selectionChanged = false;
            if (_cuaAtom != null && _cuaAtom.uid == atom.uid)
            {
                if (_debugModeJson.val) SuperController.LogMessage("Current target atom was removed");
                RestoreRigidbodyState();
                RestoreControlRigidbodyState();
                DestroyMediumObject();

                _selectedBone = null;
                _cuaAtom = null;
                _lastSelectedBonePath = null;
                _boneChoiceJson.val = "";
                selectionChanged = true;
            }
            else
            {
                if (_debugModeJson.val) SuperController.LogMessage("Another atom was removed, preserving current selection");
            }

            // This is the fix for the compiler error. Use LINQ to create a list of keys to remove.
            var keysToRemove = _assetStateMemory.Keys.Where(k => k.StartsWith(atom.uid + "|")).ToList();
            foreach (var key in keysToRemove)
            {
                _assetStateMemory.Remove(key);
                if (_debugModeJson.val) SuperController.LogMessage($"Removed state for key '{key}'");
            }

            PopulateAtomList();

            if (selectionChanged)
            {
                UpdateUIForAtomType(false);
                PopulateBoneList();
            }
            else if (_cuaAtom != null && _selectedBone == null && !string.IsNullOrEmpty(_lastSelectedBonePath))
            {
                _needsReconnection = true;
                if (_debugModeJson.val) SuperController.LogMessage("Setting reconnection needed after another atom removed");
            }
        }

        public void OnDestroy()
        {
            _isBeingDestroyed = true; // Set flag first
            SaveCurrentState();
            RestoreRigidbodyState();
            RestoreControlRigidbodyState();
            DestroyMediumObject();

            if (SuperController.singleton != null)
            {
                SuperController.singleton.onAtomAddedHandlers -= OnAtomAdded;
                SuperController.singleton.onAtomRemovedHandlers -= OnAtomRemoved;
            }

            if (_assetNameChooser != null)
            {
                _assetNameChooser.setCallbackFunction -= OnAssetNameChanged;
            }
        }

        public void Update()
        {
            try
            {
                if (_needsRestoration && enabled)
                {
                    _needsRestoration = false;
                    if (_savedJson != null)
                    {
                        if (_debugModeJson.val) SuperController.LogMessage("Update: Triggering RestoreFromJSONCoroutine.");
                        StartCoroutine(RestoreFromJsonCoroutine(_savedJson));
                        _savedJson = null;
                    }
                    else if (_debugModeJson.val)
                    {
                        SuperController.LogMessage("Update: needsRestoration was true, but savedJSON was null.");
                    }
                }


                if (_mediumTransform)
                {
                    ApplyOffsetsToMediumObject();
                }


                if (!_needsReconnection) return;
                _reconnectionTimer += Time.deltaTime;
                if (!(_reconnectionTimer >= ReconnectionInterval)) return;
                _reconnectionTimer = 0f;

                if (!_cuaAtom && !string.IsNullOrEmpty(_cuaChoiceJson.val))
                {
                    _cuaAtom = SuperController.singleton.GetAtomByUid(_cuaChoiceJson.val);
                    if (_cuaAtom)
                    {
                        if (_debugModeJson.val) SuperController.LogMessage("Reconnected to atom: " + _cuaAtom.uid);
                        FindAtomRoot();
                        UpdateUIForAtomType(_cuaAtom.type == "Person");
                    }
                    else
                    {
                        if (_debugModeJson.val) SuperController.LogMessage("Failed to reconnect to atom: " + _cuaChoiceJson.val);
                        _needsReconnection = false;
                        return;
                    }
                }

                if (_cuaAtom && !_selectedBone && !string.IsNullOrEmpty(_lastSelectedBonePath))
                {
                    if (_boneChoiceJson.choices.Count == 0 && _cuaRoot)
                    {
                        PopulateBoneList();
                    }

                    if (_debugModeJson.val) SuperController.LogMessage("Attempting to reconnect to bone: " + _lastSelectedBonePath);
                    _selectedBone = FindBoneByPath(_lastSelectedBonePath);

                    if (_selectedBone)
                    {
                        if (_debugModeJson.val) SuperController.LogMessage("Successfully reconnected to bone");
                        _needsReconnection = false;
                        ApplyCurrentRigidbodyState();

                        CreateMediumObject();
                        HandleOffsetToggleChange();

                        if (!_bonePathToDisplay.ContainsKey(_lastSelectedBonePath)) return;
                        var displayName = _bonePathToDisplay[_lastSelectedBonePath];
                        if (!_boneChoiceJson.choices.Contains(displayName) || _boneChoiceJson.val == displayName) return;
                        _switchingAtoms = true;
                        _boneChoiceJson.val = displayName;
                        _switchingAtoms = false;
                    }
                    else
                    {
                        if (_debugModeJson.val) SuperController.LogMessage("Failed reconnect attempt for bone: " + _lastSelectedBonePath);
                    }
                }
                else if (_selectedBone)
                {
                    _needsReconnection = false;
                }
                else if (string.IsNullOrEmpty(_lastSelectedBonePath))
                {
                    _needsReconnection = false;
                }
            }
            catch (Exception e)
            {
                SuperController.LogError("Exception caught in SmartDildo.Update: " + e);
                if (!_selectedBone && !string.IsNullOrEmpty(_lastSelectedBonePath))
                {
                    _needsReconnection = true;
                }
            }
        }

        public void FixedUpdate()
        {
            try
            {
                if (!_initialized || !enabled || !_enabledJson.val || !_selectedBone || !_controlPoint || !_cuaAtom || !_cuaAtom.on)
                {
                    return;
                }

                ParentingUpdate();
            }
            catch (Exception e)
            {
                SuperController.LogError("Exception caught in SmartDildo.FixedUpdate: " + e);
            }
        }

        private void HandleOffsetToggleChange()
        {
            if (!_mediumObject)
            {
                CreateMediumObject();
            }
        }

        private static float CalculateDeadZoneY(float offset, float maxPullUp, float maxPushDown)
        {
            if (offset > maxPullUp) return maxPullUp;
            if (offset < -maxPushDown) return -maxPushDown;

            return offset;
        }

        private void ParentingUpdate()
        {
            if (!_enabledJson.val || !_selectedBone || !_controlPoint || !_cuaAtom || !_cuaAtom.on) return;

            var shouldControl = _hardParentingJson.val || (!_hardParentingJson.val &&
                                                           (_syncPositionXJson.val || _syncPositionYJson.val || _syncPositionZJson.val ||
                                                            _syncRotationJson.val));
            if (!shouldControl) return;

            var targetTransform = _mediumTransform ? _mediumTransform : _selectedBone;
            if (!targetTransform) return;

            if (_followMovementJson.val || _hardParentingJson.val)
            {
                if (_hardParentingJson.val)
                {
                    if (_controlRigidbody && !_controlRigidbody.isKinematic) _controlRigidbody.isKinematic = true;
                    if (_syncPositionXJson.val)
                        _controlPoint.position = new Vector3(targetTransform.position.x, _controlPoint.position.y, _controlPoint.position.z);
                    if (_syncPositionYJson.val)
                        _controlPoint.position = new Vector3(_controlPoint.position.x, targetTransform.position.y, _controlPoint.position.z);
                    if (_syncPositionZJson.val)
                        _controlPoint.position = new Vector3(_controlPoint.position.x, _controlPoint.position.y, targetTransform.position.z);
                    if (_syncRotationJson.val) _controlPoint.rotation = targetTransform.rotation;
                }
                else
                {
                    if (!_controlRigidbody) _controlRigidbody = _controlPoint.GetComponent<Rigidbody>();
                    if (!_controlRigidbody)
                    {
                        if (_cuaAtom && _cuaAtom.type == "Person") return;
                        if (_debugModeJson.val) SuperController.LogError("Soft Reverse Parenting: Rigidbody missing on control point!");

                        return;
                    }
                    
                    if (_controlRigidbody.isKinematic)
                    {
                        if (_debugModeJson.val) SuperController.LogMessage("Soft Reverse Parenting: RB was kinematic, setting non-kinematic.");
                        _controlRigidbody.isKinematic = false;
                    }

                    var currentPos = _controlRigidbody.position;
                    var targetTransformPos = targetTransform.position;

                    var filteredTarget = new Vector3(
                        _syncPositionXJson.val ? targetTransformPos.x : currentPos.x,
                        _syncPositionYJson.val ? targetTransformPos.y : currentPos.y,
                        _syncPositionZJson.val ? targetTransformPos.z : currentPos.z
                    );

                    var shouldMoveY = false;
                    var finalY = currentPos.y;

                    if (!_syncPositionYJson.val)
                    {
                        var maxPullUpDistance = _dildoLength * _pullFactorJson.val; 
                        var maxPushDownDistance = _dildoLength * _pushFactorJson.val; 
                        
                        var currentOffset = currentPos.y - targetTransformPos.y;
                        var correction = CalculateDeadZoneY(currentOffset, maxPullUpDistance, maxPushDownDistance);

                        if (Mathf.Abs(currentOffset) > (correction > 0 ? maxPullUpDistance : maxPushDownDistance))
                        {
                            finalY = targetTransformPos.y + correction;
                            shouldMoveY = true;
                        }
                    }
                    else
                    {
                        finalY = targetTransformPos.y;
                        shouldMoveY = true;
                    }

                    _targetRotation = targetTransform.rotation;

                    var lerpFactor = 1.0f - Mathf.Exp(-_syncSpeedJson.val * 3.0f * Time.fixedDeltaTime);
                    var lerp = Vector3.Lerp(currentPos, filteredTarget, lerpFactor);

                    var newPos = _controlRigidbody.position;


                    if (_syncPositionXJson.val) newPos.x = lerp.x;
                    if (shouldMoveY) newPos.y = Mathf.Lerp(currentPos.y, finalY, lerpFactor);
                    if (_syncPositionZJson.val) newPos.z = lerp.z;

                    _controlRigidbody.MovePosition(newPos);

                    if (!_syncRotationJson.val) return;
                    var newRot = Quaternion.Slerp(_controlRigidbody.rotation, _targetRotation, lerpFactor);
                    _controlRigidbody.MoveRotation(newRot);
                }
            }
            else
            {
                if (_hardParentingJson.val)
                {
                    if (_syncPositionXJson.val)
                        _controlPoint.position = new Vector3(targetTransform.position.x, _controlPoint.position.y, _controlPoint.position.z);
                    if (_syncPositionYJson.val)
                        _controlPoint.position = new Vector3(_controlPoint.position.x, targetTransform.position.y, _controlPoint.position.z);
                    if (_syncPositionZJson.val)
                        _controlPoint.position = new Vector3(_controlPoint.position.x, _controlPoint.position.y, targetTransform.position.z);
                    if (_syncRotationJson.val) _controlPoint.rotation = targetTransform.rotation;
                }
                else if (_controlRigidbody && !_controlRigidbody.isKinematic)
                {
                    if (_syncPositionXJson.val)
                        _controlRigidbody.MovePosition(new Vector3(targetTransform.position.x, _controlRigidbody.position.y, _controlRigidbody.position.z));
                    if (_syncPositionYJson.val)
                        _controlRigidbody.MovePosition(new Vector3(_controlRigidbody.position.x, targetTransform.position.y, _controlRigidbody.position.z));
                    if (_syncPositionZJson.val)
                        _controlRigidbody.MovePosition(new Vector3(_controlRigidbody.position.x, _controlRigidbody.position.y, targetTransform.position.z));
                    if (_syncRotationJson.val) _controlRigidbody.MoveRotation(targetTransform.rotation);
                }
                else if (_controlPoint)
                {
                    if (_syncPositionXJson.val)
                        _controlPoint.position = new Vector3(targetTransform.position.x, _controlPoint.position.y, _controlPoint.position.z);
                    if (_syncPositionYJson.val)
                        _controlPoint.position = new Vector3(_controlPoint.position.x, targetTransform.position.y, _controlPoint.position.z);
                    if (_syncPositionZJson.val)
                        _controlPoint.position = new Vector3(_controlPoint.position.x, _controlPoint.position.y, targetTransform.position.z);
                    if (_syncRotationJson.val) _controlPoint.rotation = targetTransform.rotation;
                }

                _followMovementJson.val = true;
            }
        }

        public override JSONClass GetJSON(bool includePhysical = true, bool includeAppearance = true, bool forceStore = false)
        {
            SaveCurrentState(); // Save latest changes before serializing
            JSONClass json = base.GetJSON(includePhysical, includeAppearance, forceStore);

            try
            {
                if (_cuaAtom != null) json["cuaAtomUid"] = _cuaAtom.uid;

                needsStore = true;
                json["syncPositionX"] = _syncPositionXJson.val.ToString();
                json["syncPositionY"] = _syncPositionYJson.val.ToString();
                json["syncPositionZ"] = _syncPositionZJson.val.ToString();
                json["syncRotation"] = _syncRotationJson.val.ToString();
                json["offsetMultiplier"] = _offsetMultiplierJson.val.ToString(CultureInfo.InvariantCulture);
                json["offsetX"] = _offsetXJson.val.ToString(CultureInfo.InvariantCulture);
                json["offsetY"] = _offsetYJson.val.ToString(CultureInfo.InvariantCulture);
                json["offsetZ"] = _offsetZJson.val.ToString(CultureInfo.InvariantCulture);
                json["offsetRotX"] = _offsetRotXJson.val.ToString(CultureInfo.InvariantCulture);
                json["offsetRotY"] = _offsetRotYJson.val.ToString(CultureInfo.InvariantCulture);
                json["offsetRotZ"] = _offsetRotZJson.val.ToString(CultureInfo.InvariantCulture);

                if (_poseUIVisible && _savedPoses.Count > 0)
                {
                    var posesJson = new JSONClass();
                    foreach (var pair in _savedPoses)
                    {
                        posesJson[pair.Key] = pair.Value;
                    }

                    json["savedPoses"] = posesJson;
                }
                else
                {
                    if (json.HasKey("savedPoses")) json.Remove("savedPoses");
                }

                if (_assetStateMemory.Count > 0)
                {
                    var memoryJson = new JSONClass();
                    foreach (var pair in _assetStateMemory)
                    {
                        var stateJson = new JSONClass
                        {
                            ["bonePath"] = pair.Value.BonePath ?? "",
                            ["hardParenting"] = new JSONData(pair.Value.HardParenting)
                        };
                        memoryJson[pair.Key] = stateJson;
                    }

                    json["assetStateMemory"] = memoryJson;
                }

                if (forceStore) needsStore = true;
            }
            catch (Exception e)
            {
                SuperController.LogError("Exception caught in GetJSON: " + e);
            }

            return json;
        }

        public override void RestoreFromJSON(JSONClass json, bool restorePhysical = true, bool restoreAppearance = true, JSONArray presetAtoms = null,
            bool setMissingToDefault = true)
        {
            base.RestoreFromJSON(json, restorePhysical, restoreAppearance, presetAtoms, setMissingToDefault);

            try
            {
                if (json.HasKey("assetStateMemory"))
                {
                    _assetStateMemory.Clear();
                    var memoryJson = json["assetStateMemory"].AsObject;
                    foreach (KeyValuePair<string, JSONNode> pair in memoryJson)
                    {
                        var stateJson = pair.Value.AsObject;
                        var state = new AssetState
                        {
                            BonePath = stateJson["bonePath"].Value,
                            HardParenting = stateJson["hardParenting"].AsBool
                        };
                        _assetStateMemory[pair.Key] = state;
                    }

                    if (_debugModeJson != null && _debugModeJson.val)
                        SuperController.LogMessage($"RestoreJSON: Loaded {_assetStateMemory.Count} asset states.");
                }

                // Restore non-asset-specific settings before caching
                if (json.HasKey("syncPosition"))
                {
                    try
                    {
                        _syncPositionXJson.val = bool.Parse(json["syncPositionX"]);
                        _syncPositionYJson.val = bool.Parse(json["syncPositionY"]);
                        _syncPositionZJson.val = bool.Parse(json["syncPositionZ"]);
                    }
                    catch
                    {
                        // Don't do anything
                    }
                }

                if (json.HasKey("syncRotation"))
                {
                    try
                    {
                        _syncRotationJson.val = bool.Parse(json["syncRotation"]);
                    }
                    catch
                    {
                        // Don't do anything
                    }
                }

                if (json.HasKey("offsetMultiplier"))
                {
                    try
                    {
                        _offsetMultiplierJson.val = float.Parse(json["offsetMultiplier"]);
                    }
                    catch
                    {
                        // Don't do anything
                    }
                }

                if (json.HasKey("offsetX"))
                {
                    try
                    {
                        _offsetXJson.val = float.Parse(json["offsetX"]);
                    }
                    catch
                    {
                        // Don't do anything
                    }
                }

                if (json.HasKey("offsetY"))
                {
                    try
                    {
                        _offsetYJson.val = float.Parse(json["offsetY"]);
                    }
                    catch
                    {
                        // Don't do anything
                    }
                }

                if (json.HasKey("offsetZ"))
                {
                    try
                    {
                        _offsetZJson.val = float.Parse(json["offsetZ"]);
                    }
                    catch
                    {
                        // Don't do anything
                    }
                }

                if (json.HasKey("offsetRotX"))
                {
                    try
                    {
                        _offsetRotXJson.val = float.Parse(json["offsetRotX"]);
                    }
                    catch
                    {
                        // Don't do anything
                    }
                }

                if (json.HasKey("offsetRotY"))
                {
                    try
                    {
                        _offsetRotYJson.val = float.Parse(json["offsetRotY"]);
                    }
                    catch
                    {
                        // Don't do anything
                    }
                }

                if (json.HasKey("offsetRotZ"))
                {
                    try
                    {
                        _offsetRotZJson.val = float.Parse(json["offsetRotZ"]);
                    }
                    catch
                    {
                        // Don't do anything
                    }
                }

                _savedJson = JSON.Parse(json.ToString()).AsObject;

                if (_initialized && enabled)
                {
                    OnEnable();
                }
            }
            catch (Exception e)
            {
                SuperController.LogError("Exception caught in RestoreFromJSON: " + e);
            }
        }

        private IEnumerator RestoreFromJsonCoroutine(JSONClass json)
        {
            if (SuperController.singleton.isLoading)
            {
                if (_debugModeJson.val) SuperController.LogMessage("Restore waiting for scene load...");
                while (SuperController.singleton.isLoading) yield return null;
                if (_debugModeJson.val) SuperController.LogMessage("Scene load complete.");
            }

            PopulateAtomList();
            yield return null;

            var savedAtomUid = json["cuaAtomUid"]?.Value;
            if (string.IsNullOrEmpty(savedAtomUid) || !_cuaChoiceJson.choices.Contains(savedAtomUid))
            {
                if (_debugModeJson.val) SuperController.LogMessage("No valid saved atom to restore.");
                yield break;
            }

            // Step 1: Manually set the atom without triggering the interactive callback
            _cuaAtom = SuperController.singleton.GetAtomByUid(savedAtomUid);
            _cuaChoiceJson.valNoCallback = savedAtomUid;

            if (!_cuaAtom)
            {
                if (_debugModeJson.val) SuperController.LogMessage($"Failed to find atom {savedAtomUid}. Aborting restore.");
                yield break;
            }

            // Step 2: Manually set up the asset name listener and load the state for that asset
            FindAtomRoot();
            var assetLoader = _cuaAtom.GetComponentInChildren<CustomUnityAssetLoader>(true);
            if (assetLoader)
            {
                _assetNameChooser = assetLoader.GetStringChooserJSONParam("assetName");
                if (_assetNameChooser != null)
                {
                    _currentAssetNameForAtom = _assetNameChooser.val;
                    _assetNameChooser.setCallbackFunction += OnAssetNameChanged;
                }
            }

            var key = GetStateKey(_cuaAtom.uid, _currentAssetNameForAtom);
            LoadStateForKey(key); // This loads lastSelectedBonePath, hardParenting, etc. from memory

            // Step 3: Update the UI with the loaded state
            UpdateUIForAtomType(_cuaAtom.type == "Person");
            // `hardParenting` and `boneScale` are set within LoadStateForKey, we just need to update the UI
            _hardParentingJson.val = _hardParentingJson.val; // This triggers the callback and updates visuals

            // Step 4: Wait for bones to be available
            if (_cuaAtom.type == "CustomUnityAsset" || HasCustomAsset(_cuaAtom))
            {
                if (_debugModeJson.val) SuperController.LogMessage($"Waiting for bones on CUA '{_currentAssetNameForAtom}'...");
                var attempts = 0;
                while (attempts < 50) // Wait up to 5 seconds
                {
                    PopulateBoneList();
                    if (_boneChoiceJson.choices.Count > 0)
                    {
                        if (_debugModeJson.val) SuperController.LogMessage($"Bones found after {attempts * 0.1f} seconds.");
                        break;
                    }

                    yield return new WaitForSeconds(0.25f);
                    attempts++;
                }

                if (_boneChoiceJson.choices.Count == 0 && _debugModeJson.val) SuperController.LogMessage("Timed out waiting for bones.");
            }
            else
            {
                PopulateBoneList();
            }

            // Step 5: Select the bone
            if (!string.IsNullOrEmpty(_lastSelectedBonePath))
            {
                if (_bonePathToDisplay.ContainsKey(_lastSelectedBonePath))
                {
                    var displayName = _bonePathToDisplay[_lastSelectedBonePath];
                    if (_boneChoiceJson.choices.Contains(displayName))
                    {
                        _boneChoiceJson.val = displayName; // This triggers OnBoneSelected, which sets up parenting
                        HandleOffsetToggleChange(); // Ensure offsets are applied for reverse parenting on reload
                    }
                    else if (_debugModeJson.val) SuperController.LogMessage($"Saved bone '{displayName}' not in the populated list.");
                }
                else if (_debugModeJson.val) SuperController.LogMessage($"Path for saved bone '{_lastSelectedBonePath}' not found in mapping.");
            }

            // Step 6: Restore poses
            if (json.HasKey("savedPoses") && _poseUIVisible)
            {
                try
                {
                    _savedPoses.Clear();
                    var posesJson = json["savedPoses"].AsObject;
                    foreach (KeyValuePair<string, JSONNode> pair in posesJson)
                    {
                        _savedPoses[pair.Key] = pair.Value.AsObject;
                    }

                    if (_debugModeJson.val) SuperController.LogMessage($"Restored {_savedPoses.Count} poses.");
                    UpdatePoseUI();
                }
                catch (Exception e)
                {
                    SuperController.LogError($"Exception restoring poses: {e}");
                }
            }

            // Step 7: Final check for reconnection
            if (_cuaAtom && !_selectedBone && !string.IsNullOrEmpty(_lastSelectedBonePath))
            {
                if (_debugModeJson.val) SuperController.LogMessage("Initial bone connection failed on restore. Flagging for reconnection.");
                _needsReconnection = true;
            }

            if (_debugModeJson.val) SuperController.LogMessage("RestoreFromJSONCoroutine finished.");
        }
    }
}