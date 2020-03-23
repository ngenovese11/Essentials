﻿using System;
using System.Collections.Generic;
using System.Linq;
using Crestron.SimplSharp;
using Crestron.SimplSharp.CrestronIO;
using Crestron.SimplSharpPro;
using Crestron.SimplSharpPro.CrestronThread;
using Crestron.SimplSharpPro.Diagnostics;
using Crestron.SimplSharp.Reflection;

using PepperDash.Core;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Config;
using PepperDash.Essentials.Devices.Common;
using PepperDash.Essentials.DM;
using PepperDash.Essentials.Fusion;
using PepperDash.Essentials.Room.Config;
using PepperDash.Essentials.Room.MobileControl;

using Newtonsoft.Json;

namespace PepperDash.Essentials
{
    public class ControlSystem : CrestronControlSystem
    {
        HttpLogoServer LogoServer;

        List<string> LoadedAssemblies = new List<string>();

        public ControlSystem()
            : base()
        {
            Thread.MaxNumberOfUserThreads = 400;
            Global.ControlSystem = this;
            DeviceManager.Initialize(this);
        }

        /// <summary>
        /// Entry point for the program
        /// </summary>
        public override void InitializeSystem()
        {
            DeterminePlatform();

            if (Debug.DoNotLoadOnNextBoot)
            {
                CrestronConsole.AddNewConsoleCommand(s => GoWithLoad(), "go", "Loads configuration file",
                    ConsoleAccessLevelEnum.AccessOperator);
            }

            // CrestronConsole.AddNewConsoleCommand(S => { ConfigWriter.WriteConfigFile(null); }, "writeconfig", "writes the current config to a file", ConsoleAccessLevelEnum.AccessOperator);
            CrestronConsole.AddNewConsoleCommand(s =>
            {
                Debug.Console(0, Debug.ErrorLogLevel.Notice, "CONSOLE MESSAGE: {0}", s);
            }, "appdebugmessage", "Writes message to log", ConsoleAccessLevelEnum.AccessOperator);

            CrestronConsole.AddNewConsoleCommand(s =>
            {
                foreach (var tl in TieLineCollection.Default)
                    CrestronConsole.ConsoleCommandResponse("  {0}\r", tl);
            },
            "listtielines", "Prints out all tie lines", ConsoleAccessLevelEnum.AccessOperator);

            CrestronConsole.AddNewConsoleCommand(s =>
            {
                CrestronConsole.ConsoleCommandResponse
                    ("Current running configuration. This is the merged system and template configuration");
                CrestronConsole.ConsoleCommandResponse(Newtonsoft.Json.JsonConvert.SerializeObject
                    (ConfigReader.ConfigObject, Newtonsoft.Json.Formatting.Indented));
            }, "showconfig", "Shows the current running merged config", ConsoleAccessLevelEnum.AccessOperator);

            CrestronConsole.AddNewConsoleCommand(s =>
                {
                    CrestronConsole.ConsoleCommandResponse("This system can be found at the following URLs:\r" +
                        "System URL:   {0}\r" +
                        "Template URL: {1}", ConfigReader.ConfigObject.SystemUrl, ConfigReader.ConfigObject.TemplateUrl);
                }, "portalinfo", "Shows portal URLS from configuration", ConsoleAccessLevelEnum.AccessOperator);

            if (!Debug.DoNotLoadOnNextBoot)
                GoWithLoad();
        }

        /// <summary>
        /// Determines if the program is running on a processor (appliance) or server (VC-4).
        /// 
        /// Sets Global.FilePathPrefix and Global.ApplicationDirectoryPathPrefix based on platform
        /// </summary>
        public void DeterminePlatform()
        {
            try
            {
                Debug.Console(0, Debug.ErrorLogLevel.Notice, "Determining Platform....");

                string filePathPrefix;

                var dirSeparator = Global.DirectorySeparator;

                string directoryPrefix;

                directoryPrefix = Crestron.SimplSharp.CrestronIO.Directory.GetApplicationRootDirectory();

                var fullVersion = Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);

                AssemblyInformationalVersionAttribute fullVersionAtt = fullVersion[0] as AssemblyInformationalVersionAttribute;

                Global.SetAssemblyVersion(fullVersionAtt.InformationalVersion);

                if (CrestronEnvironment.DevicePlatform != eDevicePlatform.Server)   // Handles 3-series running Windows CE OS
                {
                    Debug.Console(0, Debug.ErrorLogLevel.Notice, "Starting Essentials v{0} on 3-series Appliance", Global.AssemblyVersion);

                    // Check if User/ProgramX exists
                    if (Directory.Exists(Global.ApplicationDirectoryPathPrefix + dirSeparator + "User"
                        + dirSeparator + string.Format("program{0}", InitialParametersClass.ApplicationNumber)))
                    {
                        Debug.Console(0, @"User/program{0} directory found", InitialParametersClass.ApplicationNumber);
                        filePathPrefix = directoryPrefix + dirSeparator + "User"
                        + dirSeparator + string.Format("program{0}", InitialParametersClass.ApplicationNumber) + dirSeparator;
                    }
                    // Check if Nvram/Programx exists
                    else if (Directory.Exists(directoryPrefix + dirSeparator + "Nvram"
                        + dirSeparator + string.Format("program{0}", InitialParametersClass.ApplicationNumber)))
                    {
                        Debug.Console(0, @"Nvram/program{0} directory found", InitialParametersClass.ApplicationNumber);
                        filePathPrefix = directoryPrefix + dirSeparator + "Nvram"
                        + dirSeparator + string.Format("program{0}", InitialParametersClass.ApplicationNumber) + dirSeparator;
                    }
                    // If neither exists, set path to User/ProgramX
                    else
                    {
                        Debug.Console(0, @"No previous directory found.  Using User/program{0}", InitialParametersClass.ApplicationNumber);
                        filePathPrefix = directoryPrefix + dirSeparator + "User"
                        + dirSeparator + string.Format("program{0}", InitialParametersClass.ApplicationNumber) + dirSeparator;
                    }
                }
                else   // Handles Linux OS (Virtual Control)
                {
                    Debug.Console(0, Debug.ErrorLogLevel.Notice, "Starting Essentials v{0} on Virtual Control Server", Global.AssemblyVersion);

                    // Set path to User/
                    filePathPrefix = directoryPrefix + dirSeparator + "User" + dirSeparator;
                }

                Global.SetFilePathPrefix(filePathPrefix);
            }
            catch (Exception e)
            {
                Debug.Console(0, "Unable to Determine Platform due to Exception: {0}", e.Message);
            }
        }

        /// <summary>
        /// Begins the process of loading resources including plugins and configuration data
        /// </summary>
        public void GoWithLoad()
        {
            try
            {
                Debug.SetDoNotLoadOnNextBoot(false);

                Debug.Console(0, Debug.ErrorLogLevel.Notice, "Starting Essentials load from configuration");

                // Get the loaded assembly filenames
                var appDi = new DirectoryInfo(Global.ApplicationDirectoryPathPrefix);
                var assemblyFiles = appDi.GetFiles("*.dll");

                foreach (var file in assemblyFiles)
                {
                    LoadedAssemblies.Add(file.Name);
                }

                var filesReady = SetupFilesystem();
                if (filesReady)
                {
                    Debug.Console(0, Debug.ErrorLogLevel.Notice, "Checking for plugins");
                    LoadPlugins();

                    Debug.Console(0, Debug.ErrorLogLevel.Notice, "Folder structure verified. Loading config...");
                    if (!ConfigReader.LoadConfig2())
                        return;

                    Load();
                    Debug.Console(0, Debug.ErrorLogLevel.Notice, "Essentials load complete\r" +
                        "-------------------------------------------------------------");
                }
                else
                {
                    Debug.Console(0,
                        @"----------------------------------------------
                        ------------------------------------------------
                        ------------------------------------------------
                        Essentials file structure setup completed.
                        Please load config, sgd and ir files and
                        restart program.
                        ------------------------------------------------
                        ------------------------------------------------
                        ------------------------------------------------");
                }

            }
            catch (Exception e)
            {
                Debug.Console(0, "FATAL INITIALIZE ERROR. System is in an inconsistent state:\r{0}", e);
            }

            // Notify the OS that the program intitialization has completed
            SystemMonitor.ProgramInitialization.ProgramInitializationComplete = true;

        }

        /// <summary>
        /// Loads plugins
        /// </summary>
        void LoadPlugins()
        {
            var dir = Global.FilePathPrefix + "plugins";
            if (Directory.Exists(dir))
            {
                Debug.Console(0, Debug.ErrorLogLevel.Notice, "Plugins directory found, checking for factory plugins");
                var di = new DirectoryInfo(dir);
                var zFiles = di.GetFiles("*.cplz");
                foreach (var fi in zFiles)
                {
                    Debug.Console(0, "Found cplz: {0}. Unzipping into plugins directory", fi.Name);
                    var result = CrestronZIP.Unzip(fi.FullName, di.FullName);
                    Debug.Console(0, "UnZip Result: {0}", result.ToString());
                    fi.Delete();
                }
                var files = di.GetFiles("*.dll");
                Dictionary<string, Assembly> assyList = new Dictionary<string, Assembly>();

                foreach (FileInfo fi in files)
                {
                    if (!CheckIfAssemblyExists(fi.Name))
                    {
                        try
                        {
                            var assy = Assembly.LoadFrom(fi.FullName);
                            var ver = assy.GetName().Version;
                            var verStr = string.Format("{0}.{1}.{2}.{3}", ver.Major, ver.Minor, ver.Build, ver.Revision);
                            assyList.Add(fi.FullName, assy);
                            Debug.Console(0, Debug.ErrorLogLevel.Notice, "Loaded plugin file '{0}', version {1}", fi.FullName, verStr);
                        }
                        catch
                        {
                            Debug.Console(2, "Assembly {0} is not a custom assembly", fi.FullName);
                            continue; //catching any load issues and continuing. There will be exceptions loading Crestron .dlls from the cplz Probably should do something different here
                        }
                    }
                    else
                        Debug.Console(0, Debug.ErrorLogLevel.Notice, "Skipping plugin: {0}.  There is already an assembly with that name loaded.", fi.Name);
                }
                foreach (var assy in assyList)
                {
                    // iteratate this assembly's classes, looking for "LoadPlugin()" methods
                    try
                    {
                        var types = assy.Value.GetTypes();
                        foreach (var type in types)
                        {
                            try
                            {
                                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
                                var loadPlugin = methods.FirstOrDefault(m => m.Name.Equals("LoadPlugin"));
                                if (loadPlugin != null)
                                {
                                    Debug.Console(2, "LoadPlugin method found in {0}", type.Name);

                                    var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static);

                                    var minimumVersion = fields.FirstOrDefault(p => p.Name.Equals("MinimumEssentialsFrameworkVersion"));
                                    if (minimumVersion != null)
                                    {
                                        Debug.Console(2, "MinimumEssentialsFrameworkVersion found");

                                        var minimumVersionString = minimumVersion.GetValue(null) as string;

                                        if (!string.IsNullOrEmpty(minimumVersionString))
                                        {
                                            var passed = Global.IsRunningMinimumVersionOrHigher(minimumVersionString);

                                            if (!passed)
                                            {
                                                Debug.Console(0, Debug.ErrorLogLevel.Error, "Plugin indicates minimum Essentials version {0}.  Dependency check failed.  Skipping Plugin", minimumVersionString);
                                                continue;
                                            }
                                            else
                                            {
                                                Debug.Console(0, Debug.ErrorLogLevel.Notice, "Passed plugin passed dependency check (required version {0})", minimumVersionString);
                                            }
                                        }
                                        else
                                        {
                                            Debug.Console(0, Debug.ErrorLogLevel.Warning, "MinimumEssentialsFrameworkVersion found but not set.  Loading plugin, but your mileage may vary.");
                                        }
                                    }
                                    else
                                    {
                                        Debug.Console(0, Debug.ErrorLogLevel.Warning, "MinimumEssentialsFrameworkVersion not found.  Loading plugin, but your mileage may vary.");
                                    }

                                    Debug.Console(0, Debug.ErrorLogLevel.Notice, "Adding plugin: {0}", assy.Key);
                                    loadPlugin.Invoke(null, null);
                                }
                            }
                            catch
                            {
                                Debug.Console(2, "Load Plugin not found. {0} is not a plugin assembly", assy.Value.FullName);
                                continue;
                            }

                        }
                    }
                    catch
                    {
                        Debug.Console(2, "Assembly {0} is not a custom assembly. Types cannot be loaded.", assy.Value.FullName);
                        continue;
                    }
                }
                // plugin dll will be loaded.  Any classes in plugin should have a static constructor
                // that registers that class with the Core.DeviceFactory
            }
        }

        /// <summary>
        /// Checks if the filename matches an already loaded assembly file's name
        /// </summary>
        /// <param name="filename"></param>
        /// <returns>True if file already matches loaded assembly file.</returns>
        bool CheckIfAssemblyExists(string filename)
        {
            var loadedAssembly = LoadedAssemblies.FirstOrDefault(s => s.Equals(filename));

            if (loadedAssembly != null)
                return true;
            else
                return false;
        }

        /// <summary>
        /// Verifies filesystem is set up. IR, SGD, and programX folders
        /// </summary>
        bool SetupFilesystem()
        {
            Debug.Console(0, "Verifying and/or creating folder structure");
            var configDir = Global.FilePathPrefix;
            var configExists = Directory.Exists(configDir);
            if (!configExists)
                Directory.Create(configDir);

            var irDir = Global.FilePathPrefix + "ir";
            if (!Directory.Exists(irDir))
                Directory.Create(irDir);

            var sgdDir = Global.FilePathPrefix + "sgd";
			if (!Directory.Exists(sgdDir))
				Directory.Create(sgdDir);

            var pluginDir = Global.FilePathPrefix + "plugins";
            if (!Directory.Exists(pluginDir))
                Directory.Create(pluginDir);

			return configExists;
		}

		/// <summary>
		/// 
		/// </summary>
		public void TearDown()
		{
			Debug.Console(0, "Tearing down existing system");
			DeviceManager.DeactivateAll();

			TieLineCollection.Default.Clear();

			foreach (var key in DeviceManager.GetDevices())
				DeviceManager.RemoveDevice(key);

			Debug.Console(0, "Tear down COMPLETE");
		}

		/// <summary>
		/// 
		/// </summary>
		void Load()
		{
			LoadDevices();
			LoadTieLines();
			LoadRooms();
			LoadLogoServer();

			DeviceManager.ActivateAll();

            LinkSystemMonitorToAppServer();
        }

        void LinkSystemMonitorToAppServer()
        {
            var sysMon = DeviceManager.GetDeviceForKey("systemMonitor") as PepperDash.Essentials.Core.Monitoring.SystemMonitorController;

            var appServer = DeviceManager.GetDeviceForKey("appServer") as MobileControlSystemController;


            if (sysMon != null && appServer != null)
            {
                var key = sysMon.Key + "-" + appServer.Key;
                var messenger = new PepperDash.Essentials.AppServer.Messengers.SystemMonitorMessenger
                    (key, sysMon, "/device/systemMonitor");

                messenger.RegisterWithAppServer(appServer);

                DeviceManager.AddDevice(messenger);
            }
        }

        /// <summary>
        /// Reads all devices from config and adds them to DeviceManager
        /// </summary>
        public void LoadDevices()
        {
            // Build the processor wrapper class

            DeviceManager.AddDevice(new PepperDash.Essentials.Core.Devices.CrestronProcessor("processor"));



            // Add global System Monitor device
            DeviceManager.AddDevice(new PepperDash.Essentials.Core.Monitoring.SystemMonitorController("systemMonitor"));

            foreach (var devConf in ConfigReader.ConfigObject.Devices)
            {

                try
                {
                    Debug.Console(0, Debug.ErrorLogLevel.Notice, "Creating device '{0}', type '{1}'", devConf.Key, devConf.Type);
                    // Skip this to prevent unnecessary warnings
                    if (devConf.Key == "processor")
                    {
                        if (devConf.Type.ToLower() != Global.ControlSystem.ControllerPrompt.ToLower())
                            Debug.Console(0,
                                "WARNING: Config file defines processor type as '{0}' but actual processor is '{1}'!  Some ports may not be available",
                                devConf.Type.ToUpper(), Global.ControlSystem.ControllerPrompt.ToUpper());

                        // Check if the processor is a DMPS model
                        if (this.ControllerPrompt.IndexOf("dmps", StringComparison.OrdinalIgnoreCase) > -1)
                        {
                            Debug.Console(2, "Adding DmpsRoutingController for {0} to Device Manager.", this.ControllerPrompt);

                            var propertiesConfig = JsonConvert.DeserializeObject<DM.Config.DmpsRoutingPropertiesConfig>(devConf.Properties.ToString());

                            if(propertiesConfig == null)
                                propertiesConfig =  new DM.Config.DmpsRoutingPropertiesConfig();

                            var dmpsRoutingController = DmpsRoutingController.GetDmpsRoutingController("processor-avRouting", this.ControllerPrompt, propertiesConfig);

                            DeviceManager.AddDevice(dmpsRoutingController);
                        }
                        else if (this.ControllerPrompt.IndexOf("mpc3", StringComparison.OrdinalIgnoreCase) > -1)
                        {
                            Debug.Console(2, "MPC3 processor type detected.  Adding Mpc3TouchpanelController.");

                            var butToken = devConf.Properties["buttons"];
                            if (butToken != null)
                            {
                                var buttons = butToken.ToObject<Dictionary<string, Essentials.Core.Touchpanels.KeypadButton>>();
                                var tpController = new Essentials.Core.Touchpanels.Mpc3TouchpanelController(devConf.Key, devConf.Name, Global.ControlSystem, buttons);
                                DeviceManager.AddDevice(tpController);
                            }
                            else
                            {
                                Debug.Console(0, Debug.ErrorLogLevel.Error, "Error: Unable to deserialize buttons collection for device: {0}", devConf.Key);
                            }
                            
                        }
                        else
                        {
                            Debug.Console(2, "************Processor is not DMPS type***************");
                        }

                        

                        continue;
                    }

                    // Try local factories first
                    var newDev = DeviceFactory.GetDevice(devConf);

                    if (newDev == null)
                        newDev = BridgeFactory.GetDevice(devConf);

                    // Then associated library factories
                    if (newDev == null)
                        newDev = PepperDash.Essentials.Core.DeviceFactory.GetDevice(devConf);
					if (newDev == null)
						newDev = PepperDash.Essentials.Devices.Common.DeviceFactory.GetDevice(devConf);
					if (newDev == null)
						newDev = PepperDash.Essentials.DM.DeviceFactory.GetDevice(devConf);
					if (newDev == null)
						newDev = PepperDash.Essentials.Devices.Displays.DisplayDeviceFactory.GetDevice(devConf);

					//if (newDev == null) // might want to consider the ability to override an essentials "type"
					//{
					//    // iterate plugin factories
					//    foreach (var f in FactoryObjects)
					//    {
					//        var cresFactory = f as IGetCrestronDevice;
					//        if (cresFactory != null)
					//        {
					//            newDev = cresFactory.GetDevice(devConf, this);
					//        }
					//    }
					//}

					if (newDev != null)
						DeviceManager.AddDevice(newDev);
					else
                        Debug.Console(0, Debug.ErrorLogLevel.Notice, "ERROR: Cannot load unknown device type '{0}', key '{1}'.", devConf.Type, devConf.Key);
                }
                catch (Exception e)
                {
                    Debug.Console(0, Debug.ErrorLogLevel.Notice, "ERROR: Creating device {0}. Skipping device. \r{1}", devConf.Key, e);
                }
            }
            Debug.Console(0, Debug.ErrorLogLevel.Notice, "All Devices Loaded.");

        }


        /// <summary>
        /// Helper method to load tie lines.  This should run after devices have loaded
        /// </summary>
        public void LoadTieLines()
        {
            // In the future, we can't necessarily just clear here because devices
            // might be making their own internal sources/tie lines

            var tlc = TieLineCollection.Default;
            //tlc.Clear();
            if (ConfigReader.ConfigObject.TieLines == null)
            {
                return;
            }

            foreach (var tieLineConfig in ConfigReader.ConfigObject.TieLines)
            {
                var newTL = tieLineConfig.GetTieLine();
                if (newTL != null)
                    tlc.Add(newTL);
            }

            Debug.Console(0, Debug.ErrorLogLevel.Notice, "All Tie Lines Loaded.");

        }

        /// <summary>
        /// Reads all rooms from config and adds them to DeviceManager
        /// </summary>
        public void LoadRooms()
        {
            if (ConfigReader.ConfigObject.Rooms == null)
            {
                Debug.Console(0, Debug.ErrorLogLevel.Warning, "WARNING: Configuration contains no rooms");
                return;
            }

            foreach (var roomConfig in ConfigReader.ConfigObject.Rooms)
            {
                var room = EssentialsRoomConfigHelper.GetRoomObject(roomConfig) as EssentialsRoomBase;
                if (room != null)
                {
                    if (room is EssentialsHuddleSpaceRoom)
                    {
                        DeviceManager.AddDevice(room);

                        Debug.Console(0, Debug.ErrorLogLevel.Notice, "Room is EssentialsHuddleSpaceRoom, attempting to add to DeviceManager with Fusion");
                        DeviceManager.AddDevice(new Core.Fusion.EssentialsHuddleSpaceFusionSystemControllerBase((EssentialsHuddleSpaceRoom)room, 0xf1));


                        Debug.Console(0, Debug.ErrorLogLevel.Notice, "Attempting to build Mobile Control Bridge...");
                        // Mobile Control bridge
                        var bridge = new MobileConrolEssentialsHuddleSpaceRoomBridge(room as EssentialsHuddleSpaceRoom);
                        AddBridgePostActivationHelper(bridge); // Lets things happen later when all devices are present
                        DeviceManager.AddDevice(bridge);

                        Debug.Console(0, Debug.ErrorLogLevel.Notice, "Mobile Control Bridge Added...");
                    }
                    else if (room is EssentialsHuddleVtc1Room)
                    {
                        DeviceManager.AddDevice(room);

                        Debug.Console(0, Debug.ErrorLogLevel.Notice, "Room is EssentialsHuddleVtc1Room, attempting to add to DeviceManager with Fusion");
                        DeviceManager.AddDevice(new EssentialsHuddleVtc1FusionController((EssentialsHuddleVtc1Room)room, 0xf1));

                        Debug.Console(0, Debug.ErrorLogLevel.Notice, "Attempting to build Mobile Control Bridge...");
                        // Mobile Control bridge
                        var bridge = new MobileConrolEssentialsHuddleSpaceRoomBridge(room);
                        AddBridgePostActivationHelper(bridge); // Lets things happen later when all devices are present
                        DeviceManager.AddDevice(bridge);
                    }
                    else
                    {
                        Debug.Console(0, Debug.ErrorLogLevel.Notice, "Room is NOT EssentialsRoom, attempting to add to DeviceManager w/o Fusion");
                        DeviceManager.AddDevice(room);
                    }

                }
                else
                    Debug.Console(0, Debug.ErrorLogLevel.Notice, "WARNING: Cannot create room from config, key '{0}'", roomConfig.Key);
            }

            Debug.Console(0, Debug.ErrorLogLevel.Notice, "All Rooms Loaded.");

        }

        /// <summary>
        /// Helps add the post activation steps that link bridges to main controller
        /// </summary>
        /// <param name="bridge"></param>
        void AddBridgePostActivationHelper(MobileControlBridgeBase bridge)
        {
            bridge.AddPostActivationAction(() =>
            {
                var parent = DeviceManager.AllDevices.FirstOrDefault(d => d.Key == "appServer") as MobileControlSystemController;
                if (parent == null)
                {
                    Debug.Console(0, bridge, "ERROR: Cannot connect app server room bridge. System controller not present");
                    return;
                }
                Debug.Console(0, bridge, "Linking to parent controller");
                bridge.AddParent(parent);
                parent.AddBridge(bridge);
            });
        }

        /// <summary>
        /// Fires up a logo server if not already running
        /// </summary>
        void LoadLogoServer()
        {
            try
            {
                LogoServer = new HttpLogoServer(8080, Global.DirectorySeparator + "html" + Global.DirectorySeparator + "logo");
            }
            catch (Exception)
            {
                Debug.Console(0, Debug.ErrorLogLevel.Notice, "NOTICE: Logo server cannot be started. Likely already running in another program");
            }
        }
    }
}
