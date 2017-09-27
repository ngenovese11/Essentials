﻿using System;
using System.Linq;
using System.Collections.Generic;
using Crestron.SimplSharp;
using Crestron.SimplSharpPro;
using Crestron.SimplSharpPro.UI;

using PepperDash.Core;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.SmartObjects;
using PepperDash.Essentials.Core.PageManagers;
using PepperDash.Essentials.Room.Config;

namespace PepperDash.Essentials
{
    /// <summary>
    /// 
    /// </summary>
    public class EssentialsHuddleVtc1PanelAvFunctionsDriver : PanelDriverBase, IAVDriver
    {
        CrestronTouchpanelPropertiesConfig Config;

        public enum UiDisplayMode
        {
            Presentation, AudioSetup, Call, Start
        }

        /// <summary>
        /// Whether volume ramping from this panel will show the volume
        /// gauge popup.
        /// </summary>
        public bool ShowVolumeGauge { get; set; }

        /// <summary>
        /// 
        /// </summary>
        public uint PowerOffTimeout { get; set; }

        /// <summary>
        /// 
        /// </summary>
        public string DefaultRoomKey { get; set; }

        /// <summary>
        /// 
        /// </summary>
        public EssentialsHuddleVtc1Room CurrentRoom
        {
            get { return _CurrentRoom; }
            set
            {
                SetCurrentRoom(value);
            }
        }
        EssentialsHuddleVtc1Room _CurrentRoom;

        /// <summary>
        /// For hitting feedback
        /// </summary>
        BoolInputSig CallButtonSig;
        BoolInputSig ShareButtonSig;
        BoolInputSig EndMeetingButtonSig;

        /// <summary>
        /// The parent driver for this
        /// </summary>
        PanelDriverBase Parent;

        /// <summary>
        /// All children attached to this driver.  For hiding and showing as a group.
        /// </summary>
        List<PanelDriverBase> ChildDrivers = new List<PanelDriverBase>();

        List<BoolInputSig> CurrentDisplayModeSigsInUse = new List<BoolInputSig>();

        //// Important smart objects

        /// <summary>
        /// Smart Object 3200
        /// </summary>
        SubpageReferenceList SourceStagingSrl;

        /// <summary>
        /// Smart Object 15022
        /// </summary>
        SubpageReferenceList ActivityFooterSrl;

        /// <summary>
        /// Tracks which audio page group the UI is in
        /// </summary>
        UiDisplayMode CurrentDisplayMode;

        /// <summary>
        /// The AV page mangagers that have been used, to keep them alive for later
        /// </summary>
        Dictionary<object, PageManager> PageManagers = new Dictionary<object, PageManager>();

        /// <summary>
        /// Current page manager running for a source
        /// </summary>
        PageManager CurrentSourcePageManager;

        /// <summary>
        /// Will auto-timeout a power off
        /// </summary>
        CTimer PowerOffTimer;

        /// <summary>
        /// 
        /// </summary>
        ModalDialog PowerDownModal;

        /// <summary>
        /// 
        /// </summary>
        ModalDialog WarmingCoolingModal;

        /// <summary>
        /// Represents
        /// </summary>
        public JoinedSigInterlock PopupInterlock { get; private set; }

        /// <summary>
        /// Interlock for various source, camera, call control bars. The bar above the activity footer.  This is also 
        /// used to show start page
        /// </summary>
        JoinedSigInterlock StagingBarInterlock;

        JoinedSigInterlock CallPagesInterlock;

        PepperDash.Essentials.UIDrivers.VC.EssentialsVideoCodecUiDriver VCDriver;

        public PepperDash.Essentials.Core.Touchpanels.Keyboards.HabaneroKeyboardController Keyboard { get; private set; }

        /// <summary>
        /// Constructor
        /// </summary>
        public EssentialsHuddleVtc1PanelAvFunctionsDriver(PanelDriverBase parent, CrestronTouchpanelPropertiesConfig config)
            : base(parent.TriList)
        {
            Config = config;
            Parent = parent;

            PopupInterlock = new JoinedSigInterlock(TriList);
            StagingBarInterlock = new JoinedSigInterlock(TriList);
            CallPagesInterlock = new JoinedSigInterlock(TriList);

            SourceStagingSrl = new SubpageReferenceList(TriList, UISmartObjectJoin.SourceStagingSRL, 3, 3, 3);

            ActivityFooterSrl = new SubpageReferenceList(TriList, UISmartObjectJoin.ActivityFooterSRL, 3, 3, 3);
            CallButtonSig = ActivityFooterSrl.BoolInputSig(1, 1);
            ShareButtonSig = ActivityFooterSrl.BoolInputSig(2, 1);

            SetupActivityFooterWhenRoomOff();

            ShowVolumeGauge = true;
            Keyboard = new PepperDash.Essentials.Core.Touchpanels.Keyboards.HabaneroKeyboardController(TriList);
        }

        /// <summary>
        /// Add a video codec driver to this
        /// </summary>
        /// <param name="vcd"></param>
        public void SetVideoCodecDriver(PepperDash.Essentials.UIDrivers.VC.EssentialsVideoCodecUiDriver vcd)
        {
            VCDriver = vcd;
        }

        /// <summary>
        /// 
        /// </summary>
        public override void Show()
        {
            if (CurrentRoom == null)
            {
                Debug.Console(1, "ERROR: AVUIFunctionsDriver, Cannot show. No room assigned");
                return;
            }

            var roomConf = CurrentRoom.Config;

            if (Config.HeaderStyle == UiHeaderStyle.Habanero)
            {
                TriList.SetString(UIStringJoin.CurrentRoomName, CurrentRoom.Name);
                TriList.SetSigFalseAction(UIBoolJoin.RoomHeaderButtonPress, () =>
                    PopupInterlock.ShowInterlockedWithToggle(UIBoolJoin.RoomHeaderPageVisible));
            }
            else if (Config.HeaderStyle == UiHeaderStyle.Verbose)
            {
                // room name on join 1, concat phone and sip on join 2, no button method
                TriList.SetString(UIStringJoin.CurrentRoomName, CurrentRoom.Name);
                var addr = roomConf.Addresses;
                if (addr == null) // protect from missing values by using default empties
                    addr = new EssentialsRoomAddressPropertiesConfig();
                // empty string when either missing, pipe when both showing
                TriList.SetString(UIStringJoin.RoomAddressPipeText, 
                    (string.IsNullOrEmpty(addr.PhoneNumber.Trim())
                    || string.IsNullOrEmpty(addr.SipAddress.Trim())) ? "" : " | ");
                TriList.SetString(UIStringJoin.RoomPhoneText, addr.PhoneNumber);
                TriList.SetString(UIStringJoin.RoomSipText, addr.SipAddress);
            }

            TriList.SetBool(UIBoolJoin.DateAndTimeVisible, Config.ShowDate && Config.ShowTime);
            TriList.SetBool(UIBoolJoin.DateOnlyVisible, Config.ShowDate && !Config.ShowTime);
            TriList.SetBool(UIBoolJoin.TimeOnlyVisible, !Config.ShowDate && Config.ShowTime);
            TriList.SetBool(UIBoolJoin.TopBarHabaneroVisible, true);
            TriList.SetBool(UIBoolJoin.ActivityFooterVisible, true);

            // Default to showing rooms/sources now.
            //ShowMode(UiDisplayMode.PresentationMode);
            if (CurrentRoom.OnFeedback.BoolValue)
            {
                TriList.SetBool(UIBoolJoin.SourceStagingBarVisible, true);
                TriList.SetBool(UIBoolJoin.TapToBeginVisible, false);
                TriList.SetBool(UIBoolJoin.SelectASourceVisible, false);
            }
            else
            {
                TriList.SetBool(UIBoolJoin.StartPageVisible, true);
                TriList.SetBool(UIBoolJoin.TapToBeginVisible, true);
                TriList.SetBool(UIBoolJoin.SelectASourceVisible, false);
            }
            ShowCurrentDisplayModeSigsInUse();

            // *** Header Buttons ***
            
            // Generic "close" button for these modals
            TriList.SetSigFalseAction(UIBoolJoin.InterlockedModalClosePress, PopupInterlock.HideAndClear);
            
            // Help button and popup
            if (CurrentRoom.Config.Help != null)
            {
                TriList.SetString(UIStringJoin.HelpMessage, roomConf.Help.Message);
                TriList.SetBool(UIBoolJoin.HelpPageShowCallButtonVisible, roomConf.Help.ShowCallButton);
                TriList.SetString(UIStringJoin.HelpPageCallButtonText, roomConf.Help.CallButtonText);
                if(roomConf.Help.ShowCallButton)
                    TriList.SetSigFalseAction(UIBoolJoin.HelpPageShowCallButtonPress, () => { }); // ************ FILL IN
                else
                    TriList.ClearBoolSigAction(UIBoolJoin.HelpPageShowCallButtonPress);
            }
            else // older config
            {
                TriList.SetString(UIStringJoin.HelpMessage, CurrentRoom.Config.HelpMessage);
                TriList.SetBool(UIBoolJoin.HelpPageShowCallButtonVisible, false);
                TriList.SetString(UIStringJoin.HelpPageCallButtonText, null);
                TriList.ClearBoolSigAction(UIBoolJoin.HelpPageShowCallButtonPress);
            }
            TriList.SetSigFalseAction(UIBoolJoin.HelpPress, () =>
            {
                string message = null;
                var room = DeviceManager.GetDeviceForKey(Config.DefaultRoomKey)
                    as EssentialsHuddleSpaceRoom;
                if (room != null)
                    message = room.Config.HelpMessage;
                else
                    message = "Sorry, no help message available. No room connected.";
                //TriList.StringInput[UIStringJoin.HelpMessage].StringValue = message;
                PopupInterlock.ShowInterlockedWithToggle(UIBoolJoin.HelpPageVisible);
            });
            
            // Lights button
            TriList.SetSigFalseAction(UIBoolJoin.LightsHeaderButtonPress, () => // ******************** FILL IN
                { });
            
            // Call header button
            if(roomConf.OneButtonMeeting != null && roomConf.OneButtonMeeting.Enable)
            {
                TriList.SetBool(UIBoolJoin.CalendarHeaderButtonVisible, true);
                TriList.SetBool(UIBoolJoin.CallLeftHeaderButtonVisible, true);
            }
            else
                TriList.SetBool(UIBoolJoin.CallRightHeaderButtonVisible, true);

            TriList.SetSigFalseAction(UIBoolJoin.CallHeaderButtonPress, () =>
                PopupInterlock.ShowInterlockedWithToggle(UIBoolJoin.HeaderActiveCallsListVisible));

            
            // Setup button - shows volumes with default button OR hold for tech page
            TriList.SetSigHeldAction(UIBoolJoin.GearHeaderButtonPress, 2000,
                () => PopupInterlock.ShowInterlockedWithToggle(UIBoolJoin.TechPanelSetupVisible),
                () => PopupInterlock.ShowInterlockedWithToggle(UIBoolJoin.VolumesPageVisible)); 
            TriList.SetSigFalseAction(UIBoolJoin.TechPagesExitButton, () =>
                PopupInterlock.HideAndClear());

            // Default Volume button
            TriList.SetSigFalseAction(UIBoolJoin.VolumeDefaultPress, () => // Set default volume method on room
                { });

            
            if (TriList is CrestronApp)
                TriList.BooleanInput[UIBoolJoin.GearButtonVisible].BoolValue = false;
            else
                TriList.BooleanInput[UIBoolJoin.GearButtonVisible].BoolValue = true;

            // power-related functions
            // Note: some of these are not directly-related to the huddle space UI, but are held over
            // in case
            TriList.SetSigFalseAction(UIBoolJoin.ShowPowerOffPress, PowerButtonPressed);

            TriList.SetSigFalseAction(UIBoolJoin.DisplayPowerTogglePress, () =>
            {
                if (CurrentRoom != null && CurrentRoom.DefaultDisplay is IPower)
                    (CurrentRoom.DefaultDisplay as IPower).PowerToggle();
            });

            base.Show();
        }

        /// <summary>
        /// 
        /// </summary>
        void ShowLogo()
        {
            if (CurrentRoom.LogoUrl == null)
            {
                TriList.SetBool(UIBoolJoin.LogoDefaultVisible, true);
                TriList.SetBool(UIBoolJoin.LogoUrlVisible, false);
            }
            else
            {
                TriList.SetBool(UIBoolJoin.LogoDefaultVisible, false);
                TriList.SetBool(UIBoolJoin.LogoUrlVisible, true);
                TriList.SetString(UIStringJoin.LogoUrl, _CurrentRoom.LogoUrl);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        void HideLogo()
        {
            TriList.SetBool(UIBoolJoin.LogoDefaultVisible, false);
            TriList.SetBool(UIBoolJoin.LogoUrlVisible, false);
        }

        /// <summary>
        /// 
        /// </summary>
        public override void Hide()
        {
            HideAndClearCurrentDisplayModeSigsInUse();
            TriList.BooleanInput[UIBoolJoin.TopBarHabaneroVisible].BoolValue = false;
            TriList.BooleanInput[UIBoolJoin.ActivityFooterVisible].BoolValue = false;
            TriList.BooleanInput[UIBoolJoin.StartPageVisible].BoolValue = false;
            TriList.BooleanInput[UIBoolJoin.TapToBeginVisible].BoolValue = false;
            TriList.BooleanInput[UIBoolJoin.SelectASourceVisible].BoolValue = false;
            base.Hide();
        }

        /// <summary>
        /// When the room is off, set the footer SRL
        /// </summary>
        void SetupActivityFooterWhenRoomOff()
        {
            ActivityFooterSrl.Clear();
            ActivityFooterSrl.AddItem(new SubpageReferenceListActivityItem(1, ActivityFooterSrl, 1,
                b => { if (!b) ActivityCallButtonPressed(); }));
            ActivityFooterSrl.AddItem(new SubpageReferenceListActivityItem(2, ActivityFooterSrl, 0,
                b => { if (!b) ActivityShareButtonPressed(); }));
            ActivityFooterSrl.Count = 2;
            TriList.UShortInput[UIUshortJoin.PresentationListCaretMode].UShortValue = 0;
            ShareButtonSig.BoolValue = false;
        }

        /// <summary>
        /// Sets up the footer SRL for when the room is on
        /// </summary>
        void SetupActivityFooterWhenRoomOn()
        {
            ActivityFooterSrl.Clear();
            ActivityFooterSrl.AddItem(new SubpageReferenceListActivityItem(1, ActivityFooterSrl, 1, 
                b => { if (!b) ActivityCallButtonPressed(); }));
            ActivityFooterSrl.AddItem(new SubpageReferenceListActivityItem(2, ActivityFooterSrl, 0, 
                b => { if (!b) ActivityShareButtonPressed(); }));
            ActivityFooterSrl.AddItem(new SubpageReferenceListActivityItem(3, ActivityFooterSrl,
                3, b => { if (!b) PowerButtonPressed(); }));
            ActivityFooterSrl.Count = 3;
            TriList.UShortInput[UIUshortJoin.PresentationListCaretMode].UShortValue = 1;
            EndMeetingButtonSig = ActivityFooterSrl.BoolInputSig(3, 1);

            ShareButtonSig.BoolValue = CurrentRoom.OnFeedback.BoolValue;
        } 

        /// <summary>
        /// 
        /// </summary>
        void ActivityCallButtonPressed()
        {
            if (VCDriver.IsVisible)
                return;
            CallButtonSig.BoolValue = true;
            ShareButtonSig.BoolValue = false;
            HideLogo();
            TriList.SetBool(UIBoolJoin.StartPageVisible, false);
            TriList.SetBool(UIBoolJoin.SourceStagingBarVisible, false);
            TriList.SetBool(UIBoolJoin.SelectASourceVisible, false);
            if (CurrentSourcePageManager != null)
                CurrentSourcePageManager.Hide();
            VCDriver.Show();
        }

        /// <summary>
        /// Attached to activity list share button
        /// </summary>
        void ActivityShareButtonPressed()
        {
            if (VCDriver.IsVisible)
                VCDriver.Hide();
            ShareButtonSig.BoolValue = true;
            CallButtonSig.BoolValue = false;
            TriList.SetBool(UIBoolJoin.StartPageVisible, false);
            TriList.SetBool(UIBoolJoin.SourceStagingBarVisible, true);
            // Run default source when room is off and share is pressed
            if (!CurrentRoom.OnFeedback.BoolValue)
            { 
                // If there's no default, show UI elements
                if(!CurrentRoom.RunDefaultRoute())
                    TriList.SetBool(UIBoolJoin.SelectASourceVisible, true);
            }
            else // show what's active
            {
                if (CurrentSourcePageManager != null)
                    CurrentSourcePageManager.Show();
            }
        }

        /// <summary>
        /// Shows all sigs that are in CurrentDisplayModeSigsInUse
        /// </summary>
        void ShowCurrentDisplayModeSigsInUse()
        {
            foreach (var sig in CurrentDisplayModeSigsInUse)
                sig.BoolValue = true;
        }

        /// <summary>
        /// Hides all CurrentDisplayModeSigsInUse sigs and clears the array
        /// </summary>
        void HideAndClearCurrentDisplayModeSigsInUse()
        {
            foreach (var sig in CurrentDisplayModeSigsInUse)
                sig.BoolValue = false;
            CurrentDisplayModeSigsInUse.Clear();
        }


        /// <summary>
        /// Loads the appropriate Sigs into CurrentDisplayModeSigsInUse and shows them
        /// </summary>
        void ShowCurrentSource()
        {
            if (CurrentRoom.CurrentSourceInfo == null)
                return;

            var uiDev = CurrentRoom.CurrentSourceInfo.SourceDevice as IUiDisplayInfo;
            PageManager pm = null;
            // If we need a page manager, get an appropriate one
            if (uiDev != null)
            {
                TriList.BooleanInput[UIBoolJoin.SelectASourceVisible].BoolValue = false;
                // Got an existing page manager, get it
                if (PageManagers.ContainsKey(uiDev))
                    pm = PageManagers[uiDev];
                // Otherwise make an apporiate one
                else if (uiDev is ISetTopBoxControls)
                    pm = new SetTopBoxThreePanelPageManager(uiDev as ISetTopBoxControls, TriList);
                else if (uiDev is IDiscPlayerControls)
                    pm = new DiscPlayerMediumPageManager(uiDev as IDiscPlayerControls, TriList);
                else
                    pm = new DefaultPageManager(uiDev, TriList);
                PageManagers[uiDev] = pm;
                CurrentSourcePageManager = pm;
                pm.Show();
            }
        }

        /// <summary>
        /// Called from button presses on source, where We can assume we want
        /// to change to the proper screen.
        /// </summary>
        /// <param name="key">The key name of the route to run</param>
        void UiSelectSource(string key)
        {
            // Run the route and when it calls back, show the source
            CurrentRoom.RunRouteAction(key, null);
        }

        /// <summary>
        /// 
        /// </summary>
        public void PowerButtonPressed()
        {
            if (!CurrentRoom.OnFeedback.BoolValue
                || CurrentRoom.ShutdownPromptTimer.IsRunningFeedback.BoolValue)
                return;

            CurrentRoom.StartShutdown(ShutdownType.Manual);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void ShutdownPromptTimer_HasStarted(object sender, EventArgs e)
        {
            // Do we need to check where the UI is? No?
            var timer = CurrentRoom.ShutdownPromptTimer;
            EndMeetingButtonSig.BoolValue = true;
            ShareButtonSig.BoolValue = false;

            if (CurrentRoom.ShutdownType == ShutdownType.Manual)
            {
                PowerDownModal = new ModalDialog(TriList);
                var message = string.Format("Meeting will end in {0} seconds", CurrentRoom.ShutdownPromptSeconds);

                // Attach timer things to modal
                CurrentRoom.ShutdownPromptTimer.TimeRemainingFeedback.OutputChange += ShutdownPromptTimer_TimeRemainingFeedback_OutputChange;
                CurrentRoom.ShutdownPromptTimer.PercentFeedback.OutputChange += ShutdownPromptTimer_PercentFeedback_OutputChange;

                // respond to offs by cancelling dialog
                var onFb = CurrentRoom.OnFeedback;
                EventHandler<EventArgs> offHandler = null;
                offHandler = (o, a) =>
                {
                    if (!onFb.BoolValue)
                    {
                        EndMeetingButtonSig.BoolValue = false;
                        PowerDownModal.HideDialog();
                        onFb.OutputChange -= offHandler;
                    }
                };
                onFb.OutputChange += offHandler;

                PowerDownModal.PresentModalDialog(2, "End Meeting", "Power", message, "Cancel", "End Meeting Now", true, true,
                    but =>
                    {
                        if (but != 2) // any button except for End cancels
                            timer.Cancel();
                        else
                            timer.Finish();
                    });
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void ShutdownPromptTimer_HasFinished(object sender, EventArgs e)
        {
            //Debug.Console(2, "*#*UI shutdown prompt finished");
            EndMeetingButtonSig.BoolValue = false;
            CurrentRoom.ShutdownPromptTimer.TimeRemainingFeedback.OutputChange -= ShutdownPromptTimer_TimeRemainingFeedback_OutputChange;
            CurrentRoom.ShutdownPromptTimer.PercentFeedback.OutputChange -= ShutdownPromptTimer_PercentFeedback_OutputChange;

        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void ShutdownPromptTimer_WasCancelled(object sender, EventArgs e)
        {
            //Debug.Console(2, "*#*UI shutdown prompt cancelled");
            if (PowerDownModal != null)
                PowerDownModal.HideDialog();
            EndMeetingButtonSig.BoolValue = false;
            ShareButtonSig.BoolValue = CurrentRoom.OnFeedback.BoolValue;

            CurrentRoom.ShutdownPromptTimer.TimeRemainingFeedback.OutputChange += ShutdownPromptTimer_TimeRemainingFeedback_OutputChange;
            CurrentRoom.ShutdownPromptTimer.PercentFeedback.OutputChange -= ShutdownPromptTimer_PercentFeedback_OutputChange;
        }

        void ShutdownPromptTimer_TimeRemainingFeedback_OutputChange(object sender, EventArgs e)
        {

            var message = string.Format("Meeting will end in {0} seconds", (sender as StringFeedback).StringValue);
            TriList.StringInput[ModalDialog.MessageTextJoin].StringValue = message;
        }

        void ShutdownPromptTimer_PercentFeedback_OutputChange(object sender, EventArgs e)
        {
            var value = (ushort)((sender as IntFeedback).UShortValue * 65535 / 100);
            TriList.UShortInput[ModalDialog.TimerGaugeJoin].UShortValue = value;
        }

        /// <summary>
        /// 
        /// </summary>
        void CancelPowerOffTimer()
        {
            if (PowerOffTimer != null)
            {
                PowerOffTimer.Stop();
                PowerOffTimer = null;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="state"></param>
        public void VolumeUpPress(bool state)
        {
            if (CurrentRoom.CurrentVolumeControls != null)
                CurrentRoom.CurrentVolumeControls.VolumeUp(state);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="state"></param>
        public void VolumeDownPress(bool state)
        {
            if (CurrentRoom.CurrentVolumeControls != null)
                CurrentRoom.CurrentVolumeControls.VolumeDown(state);
        }

        /// <summary>
        /// Helper for property setter. Sets the panel to the given room, latching up all functionality
        /// </summary>
        void SetCurrentRoom(EssentialsHuddleVtc1Room room)
        {
            if (_CurrentRoom == room) return;
            // Disconnect current (probably never called)
            if (_CurrentRoom != null)
            {
                // Disconnect current room
                _CurrentRoom.CurrentVolumeDeviceChange -= this.CurrentRoom_CurrentAudioDeviceChange;
                ClearAudioDeviceConnections();
                _CurrentRoom.CurrentSingleSourceChange -= this.CurrentRoom_SourceInfoChange;
                DisconnectSource(_CurrentRoom.CurrentSourceInfo);
                _CurrentRoom.ShutdownPromptTimer.HasStarted -= ShutdownPromptTimer_HasStarted;
                _CurrentRoom.ShutdownPromptTimer.HasFinished -= ShutdownPromptTimer_HasFinished;
                _CurrentRoom.ShutdownPromptTimer.WasCancelled -= ShutdownPromptTimer_WasCancelled;

                _CurrentRoom.OnFeedback.OutputChange += CurrentRoom_OnFeedback_OutputChange;
                _CurrentRoom.IsWarmingUpFeedback.OutputChange -= CurrentRoom_IsWarmingFeedback_OutputChange;
                _CurrentRoom.IsCoolingDownFeedback.OutputChange -= IsCoolingDownFeedback_OutputChange;
            }

            _CurrentRoom = room;

            if (_CurrentRoom != null)
            {
                // get the source list config and set up the source list
                var config = ConfigReader.ConfigObject.SourceLists;
                if (config.ContainsKey(_CurrentRoom.SourceListKey))
                {
                    var srcList = config[_CurrentRoom.SourceListKey];
                    // Setup sources list			
                    uint i = 1; // counter for UI list
                    foreach (var kvp in srcList)
                    {
                        var srcConfig = kvp.Value;
                        if (!srcConfig.IncludeInSourceList) // Skip sources marked this way
                            continue;

                        var actualSource = DeviceManager.GetDeviceForKey(srcConfig.SourceKey) as Device;
                        if (actualSource == null)
                        {
                            Debug.Console(1, "Cannot assign missing source '{0}' to source UI list",
                                srcConfig.SourceKey);
                            continue;
                        }
                        var routeKey = kvp.Key;
                        var item = new SubpageReferenceListSourceItem(i++, SourceStagingSrl, srcConfig,
                            b => { if (!b) UiSelectSource(routeKey); });
                        SourceStagingSrl.AddItem(item); // add to the SRL
                        item.RegisterForSourceChange(_CurrentRoom);
                    }
                    SourceStagingSrl.Count = (ushort)(i - 1);
                }
                // Name and logo
                TriList.StringInput[UIStringJoin.CurrentRoomName].StringValue = _CurrentRoom.Name;
                ShowLogo();

                // Shutdown timer
                _CurrentRoom.ShutdownPromptTimer.HasStarted += ShutdownPromptTimer_HasStarted;
                _CurrentRoom.ShutdownPromptTimer.HasFinished += ShutdownPromptTimer_HasFinished;
                _CurrentRoom.ShutdownPromptTimer.WasCancelled += ShutdownPromptTimer_WasCancelled;

                // Link up all the change events from the room
                _CurrentRoom.OnFeedback.OutputChange += CurrentRoom_OnFeedback_OutputChange;
                CurrentRoom_SyncOnFeedback();
                _CurrentRoom.IsWarmingUpFeedback.OutputChange += CurrentRoom_IsWarmingFeedback_OutputChange;
                _CurrentRoom.IsCoolingDownFeedback.OutputChange += IsCoolingDownFeedback_OutputChange;

                _CurrentRoom.CurrentVolumeDeviceChange += CurrentRoom_CurrentAudioDeviceChange;
                RefreshAudioDeviceConnections();
                _CurrentRoom.CurrentSingleSourceChange += CurrentRoom_SourceInfoChange;
                RefreshSourceInfo();
            }
            else
            {
                // Clear sigs that need to be
                TriList.StringInput[UIStringJoin.CurrentRoomName].StringValue = "Select a room";
            }
        }

        /// <summary>
        /// For room on/off changes
        /// </summary>
        void CurrentRoom_OnFeedback_OutputChange(object sender, EventArgs e)
        {
            CurrentRoom_SyncOnFeedback();
        }

        void CurrentRoom_SyncOnFeedback()
        {
            var value = _CurrentRoom.OnFeedback.BoolValue;
            //Debug.Console(2, CurrentRoom, "UI: Is on event={0}", value);
            TriList.BooleanInput[UIBoolJoin.RoomIsOn].BoolValue = value;

            if (value) //ON
            {
                SetupActivityFooterWhenRoomOn();
                TriList.BooleanInput[UIBoolJoin.SelectASourceVisible].BoolValue = false;
                TriList.BooleanInput[UIBoolJoin.SourceStagingBarVisible].BoolValue = true;
                TriList.BooleanInput[UIBoolJoin.StartPageVisible].BoolValue = false;
                TriList.BooleanInput[UIBoolJoin.VolumeSingleMute1Visible].BoolValue = true;

            }
            else
            {
                if (VCDriver.IsVisible)
                    VCDriver.Hide();
                SetupActivityFooterWhenRoomOff();
                ShowLogo();
                TriList.BooleanInput[UIBoolJoin.StartPageVisible].BoolValue = true;
                TriList.BooleanInput[UIBoolJoin.VolumeSingleMute1Visible].BoolValue = false;
                TriList.BooleanInput[UIBoolJoin.SourceStagingBarVisible].BoolValue = false;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        void CurrentRoom_IsWarmingFeedback_OutputChange(object sender, EventArgs e)
        {
            var value = CurrentRoom.IsWarmingUpFeedback.BoolValue;
            //Debug.Console(2, CurrentRoom, "UI: WARMING event={0}", value);

            if (value)
            {
                WarmingCoolingModal = new ModalDialog(TriList);
                WarmingCoolingModal.PresentModalDialog(0, "Powering Up", "Power", "<p>Room is powering up</p><p>Please wait</p>",
                    "", "", false, false, null);
            }
            else
            {
                if (WarmingCoolingModal != null)
                    WarmingCoolingModal.CancelDialog();
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void IsCoolingDownFeedback_OutputChange(object sender, EventArgs e)
        {
            var value = CurrentRoom.IsCoolingDownFeedback.BoolValue;
            //Debug.Console(2, CurrentRoom, "UI: Cooldown event={0}", value);

            if (value)
            {
                WarmingCoolingModal = new ModalDialog(TriList);
                WarmingCoolingModal.PresentModalDialog(0, "Shut Down", "Power", "<p>Room is shutting down</p><p>Please wait</p>",
                    "", "", false, false, null);
            }
            else
            {
                if (WarmingCoolingModal != null)
                    WarmingCoolingModal.CancelDialog();
            }
        }

        /// <summary>
        /// Hides source for provided source info
        /// </summary>
        /// <param name="previousInfo"></param>
        void DisconnectSource(SourceListItem previousInfo)
        {
            if (previousInfo == null) return;

            // Hide whatever is showing
            if (IsVisible)
            {
                if (CurrentSourcePageManager != null)
                {
                    CurrentSourcePageManager.Hide();
                    CurrentSourcePageManager = null;
                }
            }

            if (previousInfo == null) return;
            var previousDev = previousInfo.SourceDevice;

            // device type interfaces
            if (previousDev is ISetTopBoxControls)
                (previousDev as ISetTopBoxControls).UnlinkButtons(TriList);
            // common interfaces
            if (previousDev is IChannel)
                (previousDev as IChannel).UnlinkButtons(TriList);
            if (previousDev is IColor)
                (previousDev as IColor).UnlinkButtons(TriList);
            if (previousDev is IDPad)
                (previousDev as IDPad).UnlinkButtons(TriList);
            if (previousDev is IDvr)
                (previousDev as IDvr).UnlinkButtons(TriList);
            if (previousDev is INumericKeypad)
                (previousDev as INumericKeypad).UnlinkButtons(TriList);
            if (previousDev is IPower)
                (previousDev as IPower).UnlinkButtons(TriList);
            if (previousDev is ITransport)
                (previousDev as ITransport).UnlinkButtons(TriList);
            //if (previousDev is IRadio)
            //    (previousDev as IRadio).UnlinkButtons(this);
        }

        /// <summary>
        /// Refreshes and shows the room's current source
        /// </summary>
        void RefreshSourceInfo()
        {
            var routeInfo = CurrentRoom.CurrentSourceInfo;
            // This will show off popup too
            if (this.IsVisible)
                ShowCurrentSource();

            if (routeInfo == null)// || !CurrentRoom.OnFeedback.BoolValue)
            {
                // Check for power off and insert "Room is off"
                TriList.StringInput[UIStringJoin.CurrentSourceName].StringValue = "Room is off";
                TriList.StringInput[UIStringJoin.CurrentSourceIcon].StringValue = "Power";
                this.Hide();
                Parent.Show();
                return;
            }
            else if (CurrentRoom.CurrentSourceInfo != null)
            {
                TriList.StringInput[UIStringJoin.CurrentSourceName].StringValue = routeInfo.PreferredName;
                TriList.StringInput[UIStringJoin.CurrentSourceIcon].StringValue = routeInfo.Icon; // defaults to "blank"
            }
            else
            {
                TriList.StringInput[UIStringJoin.CurrentSourceName].StringValue = "---";
                TriList.StringInput[UIStringJoin.CurrentSourceIcon].StringValue = "Blank";
            }

            // Connect controls
            if (routeInfo.SourceDevice != null)
                ConnectControlDeviceMethods(routeInfo.SourceDevice);
        }

        /// <summary>
        /// Attach the source to the buttons and things
        /// </summary>
        void ConnectControlDeviceMethods(Device dev)
        {
            if (dev is ISetTopBoxControls)
                (dev as ISetTopBoxControls).LinkButtons(TriList);
            if (dev is IChannel)
                (dev as IChannel).LinkButtons(TriList);
            if (dev is IColor)
                (dev as IColor).LinkButtons(TriList);
            if (dev is IDPad)
                (dev as IDPad).LinkButtons(TriList);
            if (dev is IDvr)
                (dev as IDvr).LinkButtons(TriList);
            if (dev is INumericKeypad)
                (dev as INumericKeypad).LinkButtons(TriList);
            if (dev is IPower)
                (dev as IPower).LinkButtons(TriList);
            if (dev is ITransport)
                (dev as ITransport).LinkButtons(TriList);
        }

        /// <summary>
        /// Detaches the buttons and feedback from the room's current audio device
        /// </summary>
        void ClearAudioDeviceConnections()
        {
            TriList.ClearBoolSigAction(UIBoolJoin.VolumeUpPress);
            TriList.ClearBoolSigAction(UIBoolJoin.VolumeDownPress);
            TriList.ClearBoolSigAction(UIBoolJoin.Volume1ProgramMutePressAndFB);

            var fDev = CurrentRoom.CurrentVolumeControls as IBasicVolumeWithFeedback;
            if (fDev != null)
            {
                TriList.ClearUShortSigAction(UIUshortJoin.VolumeSlider1Value);
                fDev.VolumeLevelFeedback.UnlinkInputSig(
                    TriList.UShortInput[UIUshortJoin.VolumeSlider1Value]);
            }
        }

        /// <summary>
        /// Attaches the buttons and feedback to the room's current audio device
        /// </summary>
        void RefreshAudioDeviceConnections()
        {
            var dev = CurrentRoom.CurrentVolumeControls;
            if (dev != null) // connect buttons
            {
                TriList.SetBoolSigAction(UIBoolJoin.VolumeUpPress, VolumeUpPress);
                TriList.SetBoolSigAction(UIBoolJoin.VolumeDownPress, VolumeDownPress);
                TriList.SetSigFalseAction(UIBoolJoin.Volume1ProgramMutePressAndFB, dev.MuteToggle);
            }

            var fbDev = dev as IBasicVolumeWithFeedback;
            if (fbDev == null) // this should catch both IBasicVolume and IBasicVolumeWithFeeback
                TriList.UShortInput[UIUshortJoin.VolumeSlider1Value].UShortValue = 0;
            else
            {
                // slider
                TriList.SetUShortSigAction(UIUshortJoin.VolumeSlider1Value, fbDev.SetVolume);
                // feedbacks
                fbDev.MuteFeedback.LinkInputSig(TriList.BooleanInput[UIBoolJoin.Volume1ProgramMutePressAndFB]);
                fbDev.VolumeLevelFeedback.LinkInputSig(
                    TriList.UShortInput[UIUshortJoin.VolumeSlider1Value]);
            }
        }

        /// <summary>
        /// Handler for when the room's volume control device changes
        /// </summary>
        void CurrentRoom_CurrentAudioDeviceChange(object sender, VolumeDeviceChangeEventArgs args)
        {
            if (args.Type == ChangeType.WillChange)
                ClearAudioDeviceConnections();
            else // did change
                RefreshAudioDeviceConnections();
        }

        /// <summary>
        /// Handles source change
        /// </summary>
        void CurrentRoom_SourceInfoChange(EssentialsRoomBase room,
            SourceListItem info, ChangeType change)
        {
            if (change == ChangeType.WillChange)
                DisconnectSource(info);
            else
                RefreshSourceInfo();
        }
    }

    /// <summary>
    /// For hanging off various common things that child drivers might need from a parent AV driver
    /// </summary>
    public interface IAVDriver
    {
        PepperDash.Essentials.Core.Touchpanels.Keyboards.HabaneroKeyboardController Keyboard { get; }
        JoinedSigInterlock PopupInterlock { get; }
    }
}