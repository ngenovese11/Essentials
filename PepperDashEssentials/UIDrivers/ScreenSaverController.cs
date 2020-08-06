﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Crestron.SimplSharp;

using PepperDash.Core;
using PepperDash.Essentials.Core;

namespace PepperDash.Essentials
{
    /// <summary>
    /// Driver responsible for controlling the screenshaver showing the client logo, MC connection information and QR Code.  Moves the elements around to prevent screen burn in
    /// </summary>
    public class ScreenSaverController : PanelDriverBase
    {
        CTimer PositionTimer;

        uint PositionTimeoutMs;

        List<uint> PositionJoins;

        int CurrentPositionIndex;

        public ScreenSaverController(EssentialsPanelMainInterfaceDriver parent, CrestronTouchpanelPropertiesConfig config)
            : base(parent.TriList)
        {
            PositionTimeoutMs = config.ScreenSaverMovePositionIntervalMs;

            TriList.SetSigFalseAction(UIBoolJoin.MCScreenSaverClosePress, () => this.Hide());

            PositionJoins = new List<uint>() 
                { UIBoolJoin.MCScreenSaverPosition1Visible, UIBoolJoin.MCScreenSaverPosition2Visible, UIBoolJoin.MCScreenSaverPosition3Visible, UIBoolJoin.MCScreenSaverPosition4Visible };
        }

        public override void Show()
        {
            TriList.SetBool(UIBoolJoin.MCScreenSaverVisible, true);

            StartPositionTimer();

            base.Show();
        }

        public override void Hide()
        {
            PositionTimer.Stop();
            PositionTimer.Dispose();
            PositionTimer = null;

            ClearAllPositions();

            TriList.SetBool(UIBoolJoin.MCScreenSaverVisible, false);

            base.Hide();
        }



        void StartPositionTimer()
        {
            if (PositionTimer == null)
            {
                PositionTimer = new CTimer((o) => PositionTimerExpired(), PositionTimeoutMs);
                SetCurrentPosition();
            }
        }

        void PositionTimerExpired()
        {
            if (CurrentPositionIndex <= PositionJoins.Count)
            {
                CurrentPositionIndex++;
            }
            else
            {
                CurrentPositionIndex = 0;
            }
        }

        //
        void SetCurrentPosition()
        {
            ClearAllPositions();

            // Set based on current index
            TriList.SetBool(PositionJoins[CurrentPositionIndex], true);
        }

        void ClearAllPositions()
        {
            foreach (var join in PositionJoins)
            {
                TriList.SetBool(join, false);
            }
        }
    }
 
}