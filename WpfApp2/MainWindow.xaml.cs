using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Net.Http;
using System.Text.Json;
using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System;
using System.Windows.Input;
namespace WpfApp2
{
    public partial class MainWindow : Window
    {
        private static readonly HttpClient client = new HttpClient();
        private UnrealAction _selectedAction;
        // --- NEW: A global trigger to refresh the side panel ---
        public static Action RefreshMacros;
        public MainWindow()
        {
            InitializeComponent();
            LogMessage("Application started. Ready to connect to Unreal.");
            RefreshMacros = LoadCommands; // Connect the trigger
            LoadCommands(); // Load everything on startup
            //// Add your commands to the list here
            ////CommandListBox.Items.Add(new SanityCheckAction());
            //CommandListBox.Items.Add(new SetEditorCameraAction());
            //CommandListBox.Items.Add(new SetLightIntensityAction());
            ////CommandListBox.Items.Add(new SpawnEnemyAction());
            //CommandListBox.Items.Add(new InspectHelperAction());
            //CommandListBox.Items.Add(new PlayerTeleportAction()); // Add this!
            //CommandListBox.SelectedIndex = 0;
        }
        private void LoadCommands()
        {
            // Remember what tool we currently have open so we don't lose it
            string selectedName = (CommandListBox.SelectedItem as UnrealAction)?.Name;

            CommandListBox.Items.Clear();

            CommandListBox.Items.Add(new SetEditorCameraAction());
            //CommandListBox.Items.Add(new SetLightIntensityAction());
            CommandListBox.Items.Add(new SetEditorCameraMvvmAction());
            CommandListBox.Items.Add(new InspectHelperAction());
            CommandListBox.Items.Add(new PlayerTeleportAction()); // (Assuming you kept this tool)

            // 2. Load Saved Macros!
            if (File.Exists("SavedMacros.json"))
            {
                try
                {
                    var macros = JsonSerializer.Deserialize<List<SavedMacro>>(File.ReadAllText("SavedMacros.json"));
                    if (macros != null)
                    {
                        foreach (var m in macros) CommandListBox.Items.Add(new SavedMacroAction(m));
                    }
                }
                catch { }
            }

            // Restore our selection
            foreach (UnrealAction action in CommandListBox.Items)
            {
                if (action.Name == selectedName)
                {
                    CommandListBox.SelectedItem = action;
                    return;
                }
            }
            if (CommandListBox.Items.Count > 0) CommandListBox.SelectedIndex = 0;
        }
        // --- CONSOLE TOGGLE LOGIC ---
        private void ToggleConsoleBtn_Click(object sender, RoutedEventArgs e)
        {
            if (ConsoleLog.Visibility == Visibility.Visible)
            {
                // Hide the text box. Because the Grid Row is "Auto", 
                // the GroupBox will snap shut to just its header!
                ConsoleLog.Visibility = Visibility.Collapsed;
                ToggleConsoleBtn.Content = "▲";
            }
            else
            {
                // Show it again, expanding the UI
                ConsoleLog.Visibility = Visibility.Visible;
                ToggleConsoleBtn.Content = "▼";
                ConsoleLog.ScrollToEnd();
            }
        }
        private void CommandListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CommandListBox.SelectedItem is UnrealAction action)
            {
                _selectedAction = action;
                PropertyContainer.Content = action.CreateUI(client, LogMessage);
            }
        }

        private async void ExecuteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedAction == null) return;

            ExecuteButton.IsEnabled = false;
            await _selectedAction.ExecuteAsync(client, LogMessage);
            ExecuteButton.IsEnabled = true;
        }

        private void LogMessage(string message)
        {
            Dispatcher.Invoke(() =>
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                ConsoleLog.AppendText($"[{timestamp}] {message}\n");
                ConsoleLog.ScrollToEnd();
            });
        }

    }
    public abstract class BaseViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        // This method handles the "Hey, I changed!" notification automatically
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => _canExecute == null || _canExecute();
        public void Execute(object parameter) => _execute();
        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
    }
    
    public class CameraPresetViewModel : BaseViewModel
    {
        private string _presetName;
        public string PresetName
        {
            get => _presetName;
            set
            {
                _presetName = value;
                OnPropertyChanged(); // Notifies the UI to update the text
            }
        }

        // This replaces the "Save Button" click event
        public ICommand SavePresetCommand { get; }

        public CameraPresetViewModel()
        {
            SavePresetCommand = new RelayCommand(SaveToJSON);
        }

        private void SaveToJSON()
        {
            // Your existing JSON saving logic goes here!
            // It now uses the 'PresetName' property directly.
        }
    }
    // ====================================================================
    // EXTENSIBLE COMMAND ARCHITECTURE
    // ====================================================================

    public abstract class UnrealAction
    {
        public string Name { get; set; }

        public abstract UIElement CreateUI(HttpClient client, Action<string> log);
        public abstract Task ExecuteAsync(HttpClient client, Action<string> log);
        protected async Task<List<string>> GetLoadedSubLevels(HttpClient client, string worldPath, Action<string> log)
        {
            var subLevels = new List<string>();
            string url = "http://localhost:30010/remote/object/property";

            var payload = new { objectPath = worldPath, propertyName = "StreamingLevels" };

            try
            {
                var response = await SendUnrealRequest(client, url, payload);
                string responseStr = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    using (JsonDocument doc = JsonDocument.Parse(responseStr))
                    {
                        if (doc.RootElement.TryGetProperty("StreamingLevels", out JsonElement streamingArray))
                        {
                            foreach (var level in streamingArray.EnumerateArray())
                            {
                                if (level.TryGetProperty("PackageNameToLoad", out JsonElement pkgName))
                                {
                                    subLevels.Add(pkgName.GetString().Split('/').Last());
                                }
                            }
                        }
                    }
                }
                else
                {
                    // This will catch the EXACT reason Unreal rejected it (e.g., "Property not found")
                    log($"[ERROR] Sub-Level request rejected: {responseStr}");
                    log($"[DEBUG] WorldPath used: {worldPath}");
                }
            }
            catch (Exception ex)
            {
                log($"[EXCEPTION] {ex.Message}");
            }

            return subLevels;
        }
        protected StackPanel CreateInputField(string labelText, out TextBox inputTextBox, string defaultValue = "")
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            panel.Children.Add(new TextBlock { Text = labelText, Width = 120, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Normal });

            inputTextBox = new TextBox { Text = defaultValue, Width = 300, Padding = new Thickness(5), VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(inputTextBox);

            return panel;
        }
        protected async Task<string> GetProjectModuleName(HttpClient client)
        {
            string url = "http://localhost:30010/remote/object/call";
            var payload = new { objectPath = "/Script/Engine.Default__KismetSystemLibrary", functionName = "GetGameName" };
            try
            {
                var response = await client.PutAsync(url, new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
                if (response.IsSuccessStatusCode)
                {
                    using (JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
                    {
                        return doc.RootElement.GetProperty("ReturnValue").GetString().Replace(" ", "");
                    }
                }
            }
            catch { }
            return "fail"; // High-confidence fallback based on your logs
        }
        protected async Task<HttpResponseMessage> SendUnrealRequest(HttpClient client, string url, object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            // --- THE 5.4.4 FIX ---
            // This strips the "; charset=utf-8" suffix that UE 5.4.4 hates.
            content.Headers.ContentType.CharSet = null; 

            return await client.PutAsync(url, content);
        }
        protected async Task<(string FullPath, string ShortName)> GetCurrentMapInfo(HttpClient client)
        {
            string url = "http://localhost:30010/remote/object/call";

            // Attempt 1: Try to get the PIE (Play-In-Editor) World first
            var piePayload = new { objectPath = "/Script/UnrealEd.Default__UnrealEditorSubsystem", functionName = "GetGameWorld" };
            string fullPath = await TryFetchWorldPath(client, url, piePayload);

            // Attempt 2: If we aren't playing, fall back to the standard Editor World
            if (string.IsNullOrEmpty(fullPath))
            {
                var editorPayload = new { objectPath = "/Script/UnrealEd.Default__UnrealEditorSubsystem", functionName = "GetEditorWorld" };
                fullPath = await TryFetchWorldPath(client, url, editorPayload);
            }

            if (!string.IsNullOrEmpty(fullPath))
            {
                string fileName = fullPath.Split('/').Last();
                string shortName = fileName.Split('.').First();

                // Clean up the "UEDPIE_0_" prefix so the UI looks nice and readable
                string displayName = shortName.Replace("UEDPIE_0_", "");

                return (fullPath, displayName);
            }

            return ("/Game/Level.Level", "Level"); // Safe fallback
        }

        // 2. HELPER METHOD FOR FETCHING
        private async Task<string> TryFetchWorldPath(HttpClient client, string url, object payload)
        {
            try
            {
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var response = await SendUnrealRequest(client, url, payload);
                if (response.IsSuccessStatusCode)
                {
                    using (JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
                    {
                        if (doc.RootElement.TryGetProperty("ReturnValue", out JsonElement retVal))
                        {
                            string path = retVal.GetString();
                            // Unreal returns "None" or empty if the world isn't active
                            if (!string.IsNullOrWhiteSpace(path) && path != "None")
                            {
                                return path;
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        // 3. BULLETPROOF OUTLINER SCANNER
        protected async Task<List<string>> GetActorsFromActiveWorld(HttpClient client, string fullMapPath, string actorClass)
        {
            string url = "http://localhost:30010/remote/object/call";

            // Because GetCurrentMapInfo now perfectly finds the exact active world (PIE or Editor),
            // we no longer have to guess! We just append :PersistentLevel to the path!
            string contextPath = $"{fullMapPath}:PersistentLevel";

            return await FetchActorsWithContext(client, url, contextPath, actorClass);
        }
        private async Task<List<string>> FetchActorsWithContext(HttpClient client, string url, string contextPath, string actorClass)
        {
            var resultList = new List<string>();
            var payload = new
            {
                objectPath = "/Script/Engine.Default__GameplayStatics",
                functionName = "GetAllActorsOfClass",
                parameters = new { WorldContextObject = contextPath, ActorClass = actorClass }
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            try
            {
                var response = await SendUnrealRequest(client, url, payload);
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync();
                    using (JsonDocument doc = JsonDocument.Parse(json))
                    {
                        if (doc.RootElement.TryGetProperty("OutActors", out JsonElement actorsArray))
                        {
                            foreach (var actor in actorsArray.EnumerateArray())
                            {
                                string path = actor.ValueKind == JsonValueKind.String
                                    ? actor.GetString()
                                    : actor.GetProperty("objectPath").GetString();
                                resultList.Add(path);
                            }
                        }
                    }
                }
            }
            catch { /* Silently fail and return empty list to trigger fallback */ }

            return resultList;
        }
        // --- NEW: INLINE VECTOR/ROTATOR INPUT GENERATOR ---
        protected StackPanel CreateTripleInputRow(string mainLabel,
            string label1, out TextBox box1, string val1,
            string label2, out TextBox box2, string val2,
            string label3, out TextBox box3, string val3)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };

            // The main category label (e.g., "Location:")
            panel.Children.Add(new TextBlock { Text = mainLabel, Width = 120, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Normal });

            // Input 1 (e.g., X)
            panel.Children.Add(new TextBlock { Text = label1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            box1 = new TextBox { Text = val1, Width = 60, Padding = new Thickness(3), Margin = new Thickness(0, 0, 15, 0) };
            panel.Children.Add(box1);

            // Input 2 (e.g., Y)
            panel.Children.Add(new TextBlock { Text = label2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            box2 = new TextBox { Text = val2, Width = 60, Padding = new Thickness(3), Margin = new Thickness(0, 0, 15, 0) };
            panel.Children.Add(box2);

            // Input 3 (e.g., Z)
            panel.Children.Add(new TextBlock { Text = label3, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            box3 = new TextBox { Text = val3, Width = 60, Padding = new Thickness(3) };
            panel.Children.Add(box3);

            return panel;
        }
    }
    public class SetEditorCameraMvvmAction : UnrealAction
    {
        public SetEditorCameraMvvmAction() { Name = "Editor Camera (MVVM Mode)"; }

        public override UIElement CreateUI(HttpClient client, Action<string> log)
        {
            // We create the View and manually hook up its "Brain" (ViewModel)
            var view = new EditorCameraMvvmView();
            view.DataContext = new CameraPresetMvvmViewModel(client, log);
            return view;
        }

        public override async Task ExecuteAsync(HttpClient client, Action<string> log)
        {
            log("In MVVM mode, the Save logic is handled inside the tool itself.");
        }
    }

    // --- COMMAND 1: DYNAMIC LIGHT INTENSITY ---
    public class SetLightIntensityAction : UnrealAction
    {
        private ComboBox _actorDropdown;
        private TextBox _intensityInput, _mapNameInput;

        public SetLightIntensityAction()
        {
            Name = "Set Light Intensity";
        }

        public override UIElement CreateUI(HttpClient client, Action<string> log)
        {
            var panel = new StackPanel { Orientation = Orientation.Vertical };

            panel.Children.Add(CreateInputField("Current Map Name:", out _mapNameInput, "Detecting..."));

            // AUTO-FILL ON LOAD: Grabs the map name in the background
            //_ = Task.Run(async () => {
            //    string map = await GetCurrentMapName(client);
            //    Application.Current.Dispatcher.Invoke(() => _mapNameInput.Text = map);
            //});
            _ = Task.Run(async () => {
                var mapInfo = await GetCurrentMapInfo(client);
                Application.Current.Dispatcher.Invoke(() => _mapNameInput.Text = mapInfo.ShortName);
            });


            var dropPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            dropPanel.Children.Add(new TextBlock { Text = "Target Light:", Width = 120, VerticalAlignment = VerticalAlignment.Center });

            _actorDropdown = new ComboBox { Width = 200, Margin = new Thickness(0, 0, 10, 0) };
            dropPanel.Children.Add(_actorDropdown);

            var findBtn = new Button { Content = "Find Lights", Width = 90, Padding = new Thickness(5) };
            findBtn.Click += async (s, e) => await FetchLightsFromUnreal(client, log);
            dropPanel.Children.Add(findBtn);

            panel.Children.Add(dropPanel);
            panel.Children.Add(CreateInputField("Intensity (Float):", out _intensityInput, "100.0"));

            return panel;
        }

        private async Task FetchLightsFromUnreal(HttpClient client, Action<string> log)
        {
            //// AUTO-REFRESH: Grab the map name right before we scan, just in case you switched levels
            //_mapNameInput.Text = await GetCurrentMapName(client);

            //log($"Scanning map '{_mapNameInput.Text}' for Directional Lights...");

            //var lights = await GetActorsFromActiveWorld(client, _mapNameInput.Text, "/Script/Engine.DirectionalLight");

            //_actorDropdown.Items.Clear();
            var mapInfo = await GetCurrentMapInfo(client);
            _mapNameInput.Text = mapInfo.ShortName; // Update UI

            log($"Scanning '{mapInfo.ShortName}' for Directional Lights...");

            // Pass the FULL path to the scanner!
            var lights = await GetActorsFromActiveWorld(client, mapInfo.FullPath, "/Script/Engine.DirectionalLight");

            _actorDropdown.Items.Clear();
            foreach (string path in lights)
            {
                string friendlyName = path.Split('.').Last();
                string componentPath = path + ".LightComponent0";
                _actorDropdown.Items.Add(new ComboBoxItem { Content = friendlyName, Tag = componentPath });
            }

            if (_actorDropdown.Items.Count > 0)
            {
                _actorDropdown.SelectedIndex = 0;
                log($"[SUCCESS] Found {_actorDropdown.Items.Count} light(s).");
            }
            else
            {
                log("[WARNING] No Directional Lights found. Are you in the right map?");
            }
        }

        public override async Task ExecuteAsync(HttpClient client, Action<string> log)
        {
            if (_actorDropdown.SelectedItem is not ComboBoxItem selectedItem)
            {
                log("[ERROR] No light selected. Please click 'Find Lights' first.");
                return;
            }

            if (!float.TryParse(_intensityInput.Text, out float intensity))
            {
                log("[ERROR] Invalid float value for Intensity.");
                return;
            }

            string targetPath = selectedItem.Tag.ToString();
            log($"Setting intensity to {intensity} on {targetPath}...");

            string url = "http://localhost:30010/remote/object/call";

            var payload = new
            {
                objectPath = targetPath,
                functionName = "SetIntensity",
                parameters = new { NewIntensity = intensity },
                generateTransaction = true
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            try
            {
                var response = await SendUnrealRequest(client, url, payload);
                if (response.IsSuccessStatusCode) log($"[SUCCESS] Intensity Modified!");
                else log($"[ERROR] Failed: {await response.Content.ReadAsStringAsync()}");
            }
            catch (Exception ex) { log($"[EXCEPTION] {ex.Message}"); }
        }
    }

    // --- COMMAND 2: SPAWN ENEMY (Content Browser Search Version) ---
    //public class SpawnEnemyAction : UnrealAction
    //{
    //    private TextBox _searchBox, _xInput, _yInput, _zInput, _helperClassInput, _mapNameInput;
    //    private ComboBox _classDropdown;

    //    public SpawnEnemyAction() { Name = "Spawn Enemy"; }

    //    public override UIElement CreateUI(HttpClient client, Action<string> log)
    //    {
    //        var panel = new StackPanel { Orientation = Orientation.Vertical };

    //        panel.Children.Add(CreateInputField("Current Map Name:", out _mapNameInput, "Detecting..."));

    //        // AUTO-FILL ON LOAD
    //        _ = Task.Run(async () => {
    //            string map = await GetCurrentMapName(client);
    //            Application.Current.Dispatcher.Invoke(() => _mapNameInput.Text = map);
    //        });

    //        // 1. Asset Search Row
    //        var searchPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
    //        searchPanel.Children.Add(new TextBlock { Text = "Search Assets:", Width = 120, VerticalAlignment = VerticalAlignment.Center });

    //        _searchBox = new TextBox { Text = "BP_", Width = 150, Padding = new Thickness(5), Margin = new Thickness(0, 0, 10, 0) };
    //        searchPanel.Children.Add(_searchBox);

    //        var searchBtn = new Button { Content = "Search", Width = 80, Padding = new Thickness(5) };
    //        searchBtn.Click += async (s, e) => await SearchContentBrowser(client, log);
    //        searchPanel.Children.Add(searchBtn);

    //        panel.Children.Add(searchPanel);

    //        // 2. Class Dropdown Row
    //        var dropPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
    //        dropPanel.Children.Add(new TextBlock { Text = "Select Class:", Width = 120, VerticalAlignment = VerticalAlignment.Center });

    //        _classDropdown = new ComboBox { Width = 240 };
    //        dropPanel.Children.Add(_classDropdown);

    //        panel.Children.Add(dropPanel);

    //        // 3. Transform & Helper Inputs
    //        panel.Children.Add(CreateTripleInputRow("Coordinates:",
    //            "X:", out _xInput, "500.0",
    //            "Y:", out _yInput, "0.0",
    //            "Z:", out _zInput, "100.0"));
    //        panel.Children.Add(CreateInputField("Helper BP Class:", out _helperClassInput, "/Game/BP_RemoteHelper.BP_RemoteHelper_C"));

    //        return panel;
    //    }

    //    private async Task SearchContentBrowser(HttpClient client, Action<string> log)
    //    {
    //        string query = _searchBox.Text;
    //        if (string.IsNullOrWhiteSpace(query))
    //        {
    //            log("[WARNING] Please enter a search term.");
    //            return;
    //        }

    //        log($"Searching Content Browser for '{query}'...");
    //        string url = "http://localhost:30010/remote/search/assets";

    //        var payload = new { Query = query };
    //        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    //        try
    //        {
    //            var response = await client.PutAsync(url, content);
    //            string responseStr = await response.Content.ReadAsStringAsync();

    //            if (response.IsSuccessStatusCode)
    //            {
    //                using (JsonDocument doc = JsonDocument.Parse(responseStr))
    //                {
    //                    JsonElement assetsArray = doc.RootElement;
    //                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("Assets", out JsonElement innerArray))
    //                    {
    //                        assetsArray = innerArray;
    //                    }

    //                    if (assetsArray.ValueKind == JsonValueKind.Array)
    //                    {
    //                        _classDropdown.Items.Clear();

    //                        foreach (var asset in assetsArray.EnumerateArray())
    //                        {
    //                            string name = "Unknown";
    //                            if (asset.TryGetProperty("Name", out JsonElement nameElem)) name = nameElem.GetString();
    //                            else if (asset.TryGetProperty("AssetName", out JsonElement assetNameElem)) name = assetNameElem.GetString();

    //                            string objectPath = string.Empty;
    //                            if (asset.TryGetProperty("ObjectPath", out JsonElement pathElem)) objectPath = pathElem.GetString();
    //                            else if (asset.TryGetProperty("Path", out JsonElement pElem)) objectPath = pElem.GetString();

    //                            string assetClass = string.Empty;
    //                            if (asset.TryGetProperty("Class", out JsonElement classElem)) assetClass = classElem.GetString();
    //                            else if (asset.TryGetProperty("AssetClass", out JsonElement aClassElem)) assetClass = aClassElem.GetString();
    //                            else if (asset.TryGetProperty("AssetClassPath", out JsonElement acpElem)) assetClass = acpElem.ToString();

    //                            if (string.IsNullOrEmpty(objectPath)) continue;

    //                            if (!string.IsNullOrEmpty(assetClass) && assetClass.Contains("Blueprint"))
    //                            {
    //                                string spawnableClassPath = objectPath + "_C";
    //                                _classDropdown.Items.Add(new ComboBoxItem
    //                                {
    //                                    Content = name,
    //                                    Tag = spawnableClassPath
    //                                });
    //                            }
    //                        }

    //                        if (_classDropdown.Items.Count > 0)
    //                        {
    //                            _classDropdown.SelectedIndex = 0;
    //                            log($"[SUCCESS] Found {_classDropdown.Items.Count} Blueprint(s).");
    //                        }
    //                        else
    //                        {
    //                            log($"[WARNING] No Blueprints found matching '{query}'.");
    //                        }
    //                    }
    //                    else
    //                    {
    //                        log($"[ERROR] Unexpected JSON structure from Unreal.");
    //                    }
    //                }
    //            }
    //            else
    //            {
    //                log($"[ERROR] Search failed: {responseStr}");
    //            }
    //        }
    //        catch (Exception ex)
    //        {
    //            log($"[EXCEPTION] Connection failed: {ex.Message}");
    //        }
    //    }

    //    public override async Task ExecuteAsync(HttpClient client, Action<string> log)
    //    {
    //        if (_classDropdown.SelectedItem is not ComboBoxItem selectedClass)
    //        {
    //            log("[ERROR] Please search for and select a Blueprint class to spawn.");
    //            return;
    //        }

    //        if (!float.TryParse(_xInput.Text, out float x) || !float.TryParse(_yInput.Text, out float y) || !float.TryParse(_zInput.Text, out float z))
    //        {
    //            log("[ERROR] Invalid coordinates.");
    //            return;
    //        }

    //        // AUTO-REFRESH ON EXECUTE
    //        _mapNameInput.Text = await GetCurrentMapName(client);

    //        string classToSpawn = selectedClass.Tag.ToString();

    //        log($"Scanning map '{_mapNameInput.Text}' for BP_RemoteHelper...");

    //        var helpers = await GetActorsFromActiveWorld(client, _mapNameInput.Text, _helperClassInput.Text);

    //        if (helpers.Count == 0)
    //        {
    //            log("[ERROR] BP_RemoteHelper not found. Is it dragged into the map?");
    //            return;
    //        }

    //        string helperPath = helpers[0];
    //        log($"[SUCCESS] Found Helper at {helperPath.Split('.').Last()}. Spawning '{selectedClass.Content}'...");

    //        string url = "http://localhost:30010/remote/object/call";
    //        var payload = new
    //        {
    //            objectPath = helperPath,
    //            functionName = "SpawnEnemyFromAPI",
    //            parameters = new { ClassToSpawn = classToSpawn, X = x, Y = y, Z = z },
    //            generateTransaction = true
    //        };

    //        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    //        try
    //        {
    //            var response = await client.PutAsync(url, content);
    //            if (response.IsSuccessStatusCode) log($"[SUCCESS] Actor spawned successfully!");
    //            else log($"[ERROR] Call rejected: {await response.Content.ReadAsStringAsync()}");
    //        }
    //        catch (Exception ex) { log($"[EXCEPTION] {ex.Message}"); }
    //    }
    //}

    // --- COMMAND 3: INSPECT ANY ACTOR IN THE OUTLINER ---
    //public class InspectHelperAction : UnrealAction
    //{
    //    private TextBox _mapNameInput, _actorFilterInput, _functionFilterInput;
    //    private ComboBox _actorDropdown;
    //    private ListBox _functionListBox;
    //    private StackPanel _dynamicParamsPanel;
    //    private HttpClient _client;
    //    private Action<string> _log;
    //    // This dictionary will link the Parameter Name to the actual UI Control (TextBox/CheckBox)
    //    private Dictionary<string, Control> _inputControls = new Dictionary<string, Control>();

    //    // A small data class to hold all the info about the function we discover
    //    private class UFunctionDef
    //    {
    //        public string Name { get; set; }
    //        public string DisplayText { get; set; }
    //        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();
    //        public override string ToString() => DisplayText;
    //    }

    //    public InspectHelperAction() { Name = "Inspect Level Actors"; }

    //    public override UIElement CreateUI(HttpClient client, Action<string> log)
    //    {
    //        _client = client;
    //        _log = log;

    //        var panel = new StackPanel { Orientation = Orientation.Vertical };

    //        panel.Children.Add(CreateInputField("Current Map Name:", out _mapNameInput, "Detecting..."));

    //        // AUTO-FILL MAP NAME ON LOAD
    //        //_ = Task.Run(async () => {
    //        //    string map = await GetCurrentMapName(client);
    //        //    Application.Current.Dispatcher.Invoke(() => _mapNameInput.Text = map);
    //        //});
    //        _ = Task.Run(async () => {
    //            var mapInfo = await GetCurrentMapInfo(client);
    //            Application.Current.Dispatcher.Invoke(() => _mapNameInput.Text = mapInfo.ShortName);
    //        });
    //        // 1. ACTOR SEARCH ROW
    //        var searchPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
    //        searchPanel.Children.Add(new TextBlock { Text = "Actor Name Filter:", Width = 120, VerticalAlignment = VerticalAlignment.Center });
    //        _actorFilterInput = new TextBox { Width = 150, Padding = new Thickness(5), Margin = new Thickness(0, 0, 10, 0), ToolTip = "Leave blank for all actors" };
    //        searchPanel.Children.Add(_actorFilterInput);

    //        var fetchActorsBtn = new Button { Content = "Fetch Outliner", Width = 100, Padding = new Thickness(5), Background = System.Windows.Media.Brushes.SteelBlue, Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.Bold };
    //        fetchActorsBtn.Click += async (s, e) => await FetchOutlinerActors(client, log);
    //        searchPanel.Children.Add(fetchActorsBtn);
    //        panel.Children.Add(searchPanel);

    //        // 2. ACTOR DROPDOWN ROW
    //        var dropPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 15) };
    //        dropPanel.Children.Add(new TextBlock { Text = "Select Actor:", Width = 120, VerticalAlignment = VerticalAlignment.Center });
    //        _actorDropdown = new ComboBox { Width = 260 };
    //        dropPanel.Children.Add(_actorDropdown);
    //        panel.Children.Add(dropPanel);

    //        // 3. FUNCTION SEARCH ROW
    //        panel.Children.Add(CreateInputField("Function Filter:", out _functionFilterInput, ""));

    //        var inspectBtn = new Button
    //        {
    //            Content = "Discover Functions",
    //            Height = 35,
    //            Margin = new Thickness(0, 0, 0, 10),
    //            Background = System.Windows.Media.Brushes.DarkOrchid,
    //            Foreground = System.Windows.Media.Brushes.White,
    //            FontWeight = FontWeights.Bold
    //        };
    //        inspectBtn.Click += async (s, e) => await DiscoverFunctions(client, log);
    //        panel.Children.Add(inspectBtn);

    //        // 4. RESULTS UI
    //        panel.Children.Add(new TextBlock { Text = "Discovered Functions:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 5) });

    //        _functionListBox = new ListBox { Height = 120, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
    //        _functionListBox.SelectionChanged += FunctionListBox_SelectionChanged;
    //        panel.Children.Add(_functionListBox);

    //        _dynamicParamsPanel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 15, 0, 0) };
    //        panel.Children.Add(_dynamicParamsPanel);

    //        return panel;
    //    }
    //    private async Task PopulateClassDropdown(HttpClient client, string query, ComboBox targetCombo, Action<string> log)
    //    {
    //        if (string.IsNullOrWhiteSpace(query)) return;

    //        targetCombo.Items.Clear();
    //        targetCombo.Items.Add("Searching...");
    //        targetCombo.SelectedIndex = 0;

    //        string url = "http://localhost:30010/remote/search/assets";
    //        var payload = new { Query = query };

    //        try
    //        {
    //            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
    //            var response = await SendUnrealRequest(client, url, payload);

    //            if (response.IsSuccessStatusCode)
    //            {
    //                using (JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
    //                {
    //                    JsonElement assetsArray = doc.RootElement.TryGetProperty("Assets", out JsonElement inner) ? inner : doc.RootElement;
    //                    targetCombo.Items.Clear();

    //                    if (assetsArray.ValueKind == JsonValueKind.Array)
    //                    {
    //                        foreach (var asset in assetsArray.EnumerateArray())
    //                        {
    //                            // --- ROBUST PARSING (Checks all possible Unreal JSON structures) ---
    //                            string name = "Unknown";
    //                            if (asset.TryGetProperty("Name", out JsonElement nameElem)) name = nameElem.GetString();
    //                            else if (asset.TryGetProperty("AssetName", out JsonElement assetNameElem)) name = assetNameElem.GetString();

    //                            string objectPath = string.Empty;
    //                            if (asset.TryGetProperty("ObjectPath", out JsonElement pathElem)) objectPath = pathElem.GetString();
    //                            else if (asset.TryGetProperty("Path", out JsonElement pElem)) objectPath = pElem.GetString();

    //                            string assetClass = string.Empty;
    //                            if (asset.TryGetProperty("Class", out JsonElement classElem)) assetClass = classElem.GetString();
    //                            else if (asset.TryGetProperty("AssetClass", out JsonElement aClassElem)) assetClass = aClassElem.GetString();
    //                            else if (asset.TryGetProperty("AssetClassPath", out JsonElement acpElem)) assetClass = acpElem.ToString();

    //                            if (string.IsNullOrEmpty(objectPath)) continue;

    //                            // Only add if it's actually a Blueprint class
    //                            if (!string.IsNullOrEmpty(assetClass) && assetClass.Contains("Blueprint"))
    //                            {
    //                                targetCombo.Items.Add(new ComboBoxItem { Content = name, Tag = objectPath + "_C" });
    //                            }
    //                        }
    //                    }
    //                }

    //                if (targetCombo.Items.Count > 0) targetCombo.SelectedIndex = 0;
    //                else targetCombo.Items.Add("No Blueprints Found");
    //            }
    //            else
    //            {
    //                targetCombo.Items.Clear();
    //                targetCombo.Items.Add("Error");
    //                log($"[ERROR] Search API rejected the request.");
    //            }
    //        }
    //        catch (Exception ex)
    //        {
    //            targetCombo.Items.Clear();
    //            log($"[ERROR] Search failed: {ex.Message}");
    //        }
    //    }

    //    // --- FETCH ACTORS FROM OUTLINER ---
    //    private async Task FetchOutlinerActors(HttpClient client, Action<string> log)
    //    {
    //        //// Auto-refresh the map name just to be safe
    //        //_mapNameInput.Text = await GetCurrentMapName(client);

    //        //log($"Fetching all actors from map '{_mapNameInput.Text}'...");

    //        //// /Script/Engine.Actor is the base class for ALL objects in the level
    //        //var actors = await GetActorsFromActiveWorld(client, _mapNameInput.Text, "/Script/Engine.Actor");

    //        //_actorDropdown.Items.Clear();
    //        var mapInfo = await GetCurrentMapInfo(client);
    //        _mapNameInput.Text = mapInfo.ShortName;

    //        log($"Fetching all actors from '{mapInfo.ShortName}'...");

    //        // Pass the FULL path to the scanner!
    //        var actors = await GetActorsFromActiveWorld(client, mapInfo.FullPath, "/Script/Engine.Actor");

    //        _actorDropdown.Items.Clear();
    //        string filterText = _actorFilterInput.Text.ToLower().Trim();

    //        // Create a temporary list so we can sort them alphabetically
    //        var comboItems = new List<ComboBoxItem>();

    //        foreach (string path in actors)
    //        {
    //            string friendlyName = path.Split('.').Last();

    //            // Apply the user's text filter
    //            if (!string.IsNullOrEmpty(filterText) && !friendlyName.ToLower().Contains(filterText))
    //                continue;

    //            comboItems.Add(new ComboBoxItem { Content = friendlyName, Tag = path });
    //        }

    //        // Sort alphabetically and add to UI
    //        foreach (var item in comboItems.OrderBy(i => i.Content.ToString()))
    //        {
    //            _actorDropdown.Items.Add(item);
    //        }

    //        if (_actorDropdown.Items.Count > 0)
    //        {
    //            _actorDropdown.SelectedIndex = 0;
    //            log($"[SUCCESS] Loaded {_actorDropdown.Items.Count} actors into the dropdown.");
    //        }
    //        else
    //        {
    //            log("[WARNING] No actors found matching that filter.");
    //        }
    //    }

    //    private string CleanUnrealTypeName(string rawType)
    //    {
    //        if (string.IsNullOrEmpty(rawType)) return "Unknown";

    //        return rawType
    //            .Replace("Property", "")      // FloatProperty -> Float, BoolProperty -> Bool
    //            .Replace("Int32", "Int")       // Int32 -> Int
    //            .Replace("FVector", "Vector")  // FVector -> Vector
    //            .Replace("FRotator", "Rotator")
    //            .Replace("FString", "String");
    //    }

    //    // --- DISCOVER FUNCTIONS ON SELECTED ACTOR ---
    //    private async Task DiscoverFunctions(HttpClient client, Action<string> log)
    //    {
    //        if (_actorDropdown.SelectedItem is not ComboBoxItem selectedActor)
    //        {
    //            log("[ERROR] Please fetch and select an actor from the dropdown first.");
    //            return;
    //        }

    //        _functionListBox.Items.Clear();
    //        _dynamicParamsPanel.Children.Clear();
    //        _inputControls.Clear();

    //        string actorPath = selectedActor.Tag.ToString();
    //        log($"Describing {selectedActor.Content}...");

    //        string url = "http://localhost:30010/remote/object/describe";
    //        var payload = new { objectPath = actorPath };
    //        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    //        try
    //        {
    //            var response = await SendUnrealRequest(client, url, payload);
    //            string responseStr = await response.Content.ReadAsStringAsync();

    //            if (response.IsSuccessStatusCode)
    //            {
    //                using (JsonDocument doc = JsonDocument.Parse(responseStr))
    //                {
    //                    if (doc.RootElement.TryGetProperty("Functions", out JsonElement functionsArray))
    //                    {
    //                        string filterText = _functionFilterInput.Text.ToLower().Trim();

    //                        foreach (var func in functionsArray.EnumerateArray())
    //                        {
    //                            string funcName = func.GetProperty("Name").GetString();

    //                            if (!string.IsNullOrEmpty(filterText) && !funcName.ToLower().Contains(filterText))
    //                                continue;

    //                            UFunctionDef funcDef = new UFunctionDef { Name = funcName };
    //                            List<string> displayArgs = new List<string>();

    //                            if (func.TryGetProperty("Arguments", out JsonElement argsArray))
    //                            {
    //                                foreach (var arg in argsArray.EnumerateArray())
    //                                {
    //                                    string argName = arg.GetProperty("Name").GetString();
    //                                    string argType = "Unknown";

    //                                    if (arg.TryGetProperty("Type", out JsonElement typeDirect))
    //                                        argType = typeDirect.GetString();
    //                                    else if (arg.TryGetProperty("Property", out JsonElement prop))
    //                                    {
    //                                        if (prop.TryGetProperty("Type", out JsonElement t)) argType = t.GetString();
    //                                        else if (prop.TryGetProperty("FieldClass", out JsonElement fc)) argType = fc.GetString();
    //                                        else if (prop.TryGetProperty("CPPType", out JsonElement cpp)) argType = cpp.GetString();
    //                                    }

    //                                    argType = CleanUnrealTypeName(argType);
    //                                    funcDef.Parameters.Add(argName, argType);
    //                                    displayArgs.Add($"{argType} {argName}");
    //                                }
    //                            }

    //                            funcDef.DisplayText = $"Function: {funcName}({string.Join(", ", displayArgs)})";
    //                            _functionListBox.Items.Add(funcDef);
    //                        }
    //                        log($"[SUCCESS] Discovered {_functionListBox.Items.Count} matching function(s).");
    //                    }
    //                }
    //            }
    //            else log($"[ERROR] Describe failed: {responseStr}");
    //        }
    //        catch (Exception ex) { log($"[EXCEPTION] {ex.Message}"); }
    //    }

    //    private void FunctionListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    //    {
    //        _dynamicParamsPanel.Children.Clear();
    //        _inputControls.Clear();

    //        if (_functionListBox.SelectedItem is not UFunctionDef selectedFunc) return;

    //        if (selectedFunc.Parameters.Count > 0)
    //        {
    //            _dynamicParamsPanel.Children.Add(new TextBlock { Text = "Function Parameters:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10) });

    //            foreach (var param in selectedFunc.Parameters)
    //            {
    //                string paramName = param.Key;
    //                string paramType = param.Value;

    //                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
    //                row.Children.Add(new TextBlock { Text = $"{paramName} ({paramType}):", Width = 150, VerticalAlignment = VerticalAlignment.Center });

    //                if (paramType == "bool")
    //                {
    //                    var checkBox = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
    //                    row.Children.Add(checkBox);
    //                    _inputControls.Add(paramName, checkBox);
    //                }
    //                else if (paramType == "Class" || paramType == "SoftClass" || paramType == "UClass")
    //                {
    //                    // --- NEW: INLINE CLASS SEARCH UI ---
    //                    var classSearchPanel = new StackPanel { Orientation = Orientation.Horizontal };

    //                    var searchBox = new TextBox { Width = 70, Padding = new Thickness(3), Margin = new Thickness(0, 0, 5, 0), Text = "BP_" };
    //                    var searchBtn = new Button { Content = "🔍", Width = 30, Margin = new Thickness(0, 0, 5, 0) };
    //                    var classCombo = new ComboBox { Width = 140 };

    //                    searchBtn.Click += async (s, ev) => await PopulateClassDropdown(_client, searchBox.Text, classCombo, _log);

    //                    classSearchPanel.Children.Add(searchBox);
    //                    classSearchPanel.Children.Add(searchBtn);
    //                    classSearchPanel.Children.Add(classCombo);

    //                    row.Children.Add(classSearchPanel);

    //                    // We store the ComboBox in our dictionary so we can read its selected Tag later
    //                    _inputControls.Add(paramName, classCombo);
    //                }
    //                else
    //                {
    //                    var textBox = new TextBox { Width = 250, Padding = new Thickness(3) };
    //                    row.Children.Add(textBox);
    //                    _inputControls.Add(paramName, textBox);
    //                }

    //                _dynamicParamsPanel.Children.Add(row);
    //            }
    //        }
    //        else
    //        {
    //            _dynamicParamsPanel.Children.Add(new TextBlock { Text = "This function requires no parameters.", FontStyle = FontStyles.Italic, Foreground = System.Windows.Media.Brushes.Gray });
    //        }
    //    }

    //    public override async Task ExecuteAsync(HttpClient client, Action<string> log)
    //    {
    //        if (_actorDropdown.SelectedItem is not ComboBoxItem selectedActor)
    //        {
    //            log("[ERROR] Please select an Actor from the dropdown first.");
    //            return;
    //        }

    //        if (_functionListBox.SelectedItem is not UFunctionDef selectedFunc)
    //        {
    //            log("[ERROR] Please select a function from the list first.");
    //            return;
    //        }

    //        log($"Executing {selectedFunc.Name} on {selectedActor.Content}...");

    //        var dynamicParameters = new Dictionary<string, object>();

    //        foreach (var param in selectedFunc.Parameters)
    //        {
    //            string pName = param.Key;
    //            string pType = param.Value;

    //            if (_inputControls.TryGetValue(pName, out Control uiControl))
    //            {
    //                try
    //                {
    //                    // 2. Parse the UI inputs back into proper C# types
    //                    if (uiControl is CheckBox cb)
    //                    {
    //                        dynamicParameters.Add(pName, cb.IsChecked ?? false);
    //                    }
    //                    else if (uiControl is ComboBox combo) // <-- NEW: Read from our Class Dropdown
    //                    {
    //                        if (combo.SelectedItem is ComboBoxItem selectedClass)
    //                        {
    //                            // The Tag holds the perfect "/Game/Path/BP_Name.BP_Name_C" string
    //                            dynamicParameters.Add(pName, selectedClass.Tag.ToString());
    //                        }
    //                        else
    //                        {
    //                            log($"[ERROR] Please search for and select a valid Class for '{pName}'.");
    //                            return;
    //                        }
    //                    }
    //                    else if (uiControl is TextBox tb)
    //                    {
    //                        if (pType == "Float" || pType == "Double") dynamicParameters.Add(pName, float.Parse(tb.Text));
    //                        else if (pType == "Int" || pType == "Int64" || pType == "Byte") dynamicParameters.Add(pName, int.Parse(tb.Text));
    //                        else dynamicParameters.Add(pName, tb.Text);
    //                    }
    //                }
    //                catch
    //                {
    //                    log($"[ERROR] Invalid input for parameter '{pName}'. Expected {pType}.");
    //                    return;
    //                }
    //            }
    //        }

    //        string url = "http://localhost:30010/remote/object/call";
    //        var payload = new
    //        {
    //            objectPath = selectedActor.Tag.ToString(), // Target the specific actor!
    //            functionName = selectedFunc.Name,
    //            parameters = dynamicParameters,
    //            generateTransaction = true
    //        };

    //        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    //        try
    //        {
    //            var response = await SendUnrealRequest(client, url, payload);
    //            string responseStr = await response.Content.ReadAsStringAsync();

    //            if (response.IsSuccessStatusCode) log($"[SUCCESS] Function {selectedFunc.Name} executed successfully!");
    //            else log($"[ERROR] Call rejected: {responseStr}");
    //        }
    //        catch (Exception ex) { log($"[EXCEPTION] {ex.Message}"); }
    //    }
    //}
    // --- COMMAND 3: INSPECT LEVEL ACTORS (MACRO BUILDER) ---
    public class InspectHelperAction : UnrealAction
    {
        private TextBox _mapNameInput, _actorFilterInput, _functionFilterInput, _macroNameInput;
        private ComboBox _actorDropdown;
        private ListBox _functionListBox;
        private StackPanel _dynamicParamsPanel;

        private HttpClient _client;
        private Action<string> _log;
        private Dictionary<string, Control> _inputControls = new Dictionary<string, Control>();

        // Notice: We removed the old SavedMacro class from here! 
        // It now relies on the global one at the bottom of your file.

        private class UFunctionDef
        {
            public string Name { get; set; }
            public string DisplayText { get; set; }
            public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();
            public override string ToString() => DisplayText;
        }

        public InspectHelperAction() { Name = "Inspect Level Actors"; }

        public override UIElement CreateUI(HttpClient client, Action<string> log)
        {
            _client = client;
            _log = log;

            var panel = new StackPanel { Orientation = Orientation.Vertical };

            panel.Children.Add(CreateInputField("Current Map Name:", out _mapNameInput, "Detecting..."));

            _ = Task.Run(async () => {
                var mapInfo = await GetCurrentMapInfo(client);
                Application.Current.Dispatcher.Invoke(() => _mapNameInput.Text = mapInfo.ShortName);
            });
            // --- ADD THIS NEW CODE ---
            var subLevelBtn = new Button
            {
                Content = "Detect Sub-Levels",
                Width = 130,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(120, 5, 0, 15),
                Background = System.Windows.Media.Brushes.DarkSlateGray,
                Foreground = System.Windows.Media.Brushes.White
            };
            subLevelBtn.Click += async (s, e) => await CheckSubLevels();
            panel.Children.Add(subLevelBtn);
            // -------------------------
            var searchPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            searchPanel.Children.Add(new TextBlock { Text = "Actor Name Filter:", Width = 120, VerticalAlignment = VerticalAlignment.Center });
            _actorFilterInput = new TextBox { Width = 150, Padding = new Thickness(5), Margin = new Thickness(0, 0, 10, 0) };
            searchPanel.Children.Add(_actorFilterInput);

            var fetchActorsBtn = new Button { Content = "Fetch Outliner", Width = 100, Padding = new Thickness(5), Background = System.Windows.Media.Brushes.SteelBlue, Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.Bold };
            fetchActorsBtn.Click += async (s, e) => await FetchOutlinerActors();
            searchPanel.Children.Add(fetchActorsBtn);
            panel.Children.Add(searchPanel);

            // 2. ACTOR DROPDOWN ROW
            var dropPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 15) };
            dropPanel.Children.Add(new TextBlock { Text = "Select Actor:", Width = 120, VerticalAlignment = VerticalAlignment.Center });

            // Made the dropdown slightly narrower to fit the button
            _actorDropdown = new ComboBox { Width = 220 };
            dropPanel.Children.Add(_actorDropdown);

            // --- THE NEW GET LABELS BUTTON ---
            var getLabelsBtn = new Button
            {
                Content = "🏷️ Get Labels",
                Width = 90,
                Margin = new Thickness(10, 0, 0, 0),
                Background = System.Windows.Media.Brushes.DarkOrange,
                Foreground = System.Windows.Media.Brushes.White,
                FontWeight = FontWeights.Bold
            };
            getLabelsBtn.Click += async (s, e) => await ConvertToDisplayNames();
            dropPanel.Children.Add(getLabelsBtn);

            panel.Children.Add(dropPanel);

            panel.Children.Add(CreateInputField("Function Filter:", out _functionFilterInput, ""));

            var inspectBtn = new Button { Content = "Discover Functions", Height = 35, Margin = new Thickness(0, 0, 0, 10), Background = System.Windows.Media.Brushes.DarkOrchid, Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.Bold };
            inspectBtn.Click += async (s, e) => await DiscoverFunctions();
            panel.Children.Add(inspectBtn);

            panel.Children.Add(new TextBlock { Text = "Discovered Functions:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 5) });
            _functionListBox = new ListBox { Height = 120, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
            _functionListBox.SelectionChanged += FunctionListBox_SelectionChanged;
            panel.Children.Add(_functionListBox);

            _dynamicParamsPanel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 15, 0, 15) };
            panel.Children.Add(_dynamicParamsPanel);

            // --- THE NEW SAVE PANEL ---
            var savePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            savePanel.Children.Add(new TextBlock { Text = "Command Name:", Width = 110, VerticalAlignment = VerticalAlignment.Center });
            _macroNameInput = new TextBox { Width = 150, Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(3) };
            savePanel.Children.Add(_macroNameInput);

            var saveMacroBtn = new Button { Content = "💾 Save as New Tool in Side Panel", Width = 210, Background = System.Windows.Media.Brushes.DarkGoldenrod, Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.Bold };
            saveMacroBtn.Click += (s, e) => SaveCurrentSetupAsMacro();
            savePanel.Children.Add(saveMacroBtn);

            panel.Children.Add(savePanel);

            return panel;
        }

        private void SaveCurrentSetupAsMacro()
        {
            if (_actorDropdown.SelectedItem is not ComboBoxItem selectedActor || _functionListBox.SelectedItem is not UFunctionDef selectedFunc)
            {
                _log("[ERROR] You must select an Actor and a Function to save a command.");
                return;
            }

            string name = string.IsNullOrWhiteSpace(_macroNameInput.Text) ? $"{selectedFunc.Name} Setup" : _macroNameInput.Text;

            var newMacro = new SavedMacro
            {
                Name = name,
                ActorPath = selectedActor.Tag.ToString(),
                FunctionName = selectedFunc.Name
            };

            // Read the current UI to grab the types and default values
            foreach (var param in selectedFunc.Parameters)
            {
                string pName = param.Key;
                newMacro.ParameterTypes.Add(pName, param.Value); // Save the type

                if (_inputControls.TryGetValue(pName, out Control uiControl))
                {
                    if (uiControl is CheckBox cb) newMacro.DefaultValues.Add(pName, (cb.IsChecked ?? false).ToString());
                    else if (uiControl is ComboBox combo && combo.SelectedItem is ComboBoxItem classItem) newMacro.DefaultValues.Add(pName, classItem.Tag.ToString());
                    else if (uiControl is TextBox tb) newMacro.DefaultValues.Add(pName, tb.Text);
                    else newMacro.DefaultValues.Add(pName, "");
                }
            }

            // Load existing, append, and save
            List<SavedMacro> macros = new List<SavedMacro>();
            if (File.Exists("SavedMacros.json"))
            {
                try { macros = JsonSerializer.Deserialize<List<SavedMacro>>(File.ReadAllText("SavedMacros.json")) ?? new List<SavedMacro>(); } catch { }
            }

            macros.RemoveAll(m => m.Name == name); // Overwrite if same name
            macros.Add(newMacro);
            File.WriteAllText("SavedMacros.json", JsonSerializer.Serialize(macros));

            _macroNameInput.Text = "";
            _log($"💾 Saved Custom Command: '{name}'");

            // --- THE MAGIC TRIGGER ---
            MainWindow.RefreshMacros?.Invoke();
        }

        public override async Task ExecuteAsync(HttpClient client, Action<string> log)
        {
            if (_actorDropdown.SelectedItem is not ComboBoxItem selectedActor || _functionListBox.SelectedItem is not UFunctionDef selectedFunc)
            {
                log("[ERROR] Select an Actor and Function first.");
                return;
            }

            var dynamicParameters = new Dictionary<string, object>();
            foreach (var param in selectedFunc.Parameters)
            {
                string pName = param.Key; string pType = param.Value.ToLower();
                if (_inputControls.TryGetValue(pName, out Control uiControl))
                {
                    try
                    {
                        if (uiControl is CheckBox cb) dynamicParameters.Add(pName, cb.IsChecked ?? false);
                        else if (uiControl is ComboBox combo)
                        {
                            if (combo.SelectedItem is ComboBoxItem cls) dynamicParameters.Add(pName, cls.Tag.ToString());
                            else { log($"[ERROR] Select a class for '{pName}'."); return; }
                        }
                        else if (uiControl is TextBox tb)
                        {
                            if (pType == "float" || pType == "double") dynamicParameters.Add(pName, float.Parse(tb.Text, System.Globalization.CultureInfo.InvariantCulture));
                            else if (pType == "int" || pType == "int64" || pType == "byte") dynamicParameters.Add(pName, int.Parse(tb.Text));
                            else if (pType == "bool" || pType == "boolean") dynamicParameters.Add(pName, bool.Parse(tb.Text.ToLower()));
                            else dynamicParameters.Add(pName, tb.Text);
                        }
                    }
                    catch { log($"[ERROR] Invalid input for '{pName}'. Expected {pType}."); return; }
                }
            }

            log($"Executing {selectedFunc.Name} on {selectedActor.Content}...");
            string url = "http://localhost:30010/remote/object/call";
            var payload = new { objectPath = selectedActor.Tag.ToString(), functionName = selectedFunc.Name, parameters = dynamicParameters, generateTransaction = true };

            //try
            //{
            //    var response = await SendUnrealRequest(client, url, payload);
            //    if (response.IsSuccessStatusCode) log($"✅ Function executed!");
            //    else log($"❌ Failed: {await response.Content.ReadAsStringAsync()}");
            //}
            //catch (Exception ex) { log($"[EXCEPTION] {ex.Message}"); }
            try
            {
                var response = await SendUnrealRequest(client, url, payload);
                string responseStr = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    using (JsonDocument doc = JsonDocument.Parse(responseStr))
                    {
                        var root = doc.RootElement;

                        // 1. Check if the Blueprint function has a standard "Return Value"
                        if (root.TryGetProperty("ReturnValue", out JsonElement retVal))
                        {
                            log($"✅ Success! Result: {retVal.ToString()}");
                        }
                        // 2. Check if it returned multiple Out parameters (Unreal puts them in the JSON)
                        else if (root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Any())
                        {
                            // We will format the JSON string to make it readable in the console
                            string prettyOutput = JsonSerializer.Serialize(
                                JsonSerializer.Deserialize<object>(responseStr),
                                new JsonSerializerOptions { WriteIndented = true }
                            );
                            log($"✅ Executed! Outputs:\n{prettyOutput}");
                        }
                        // 3. It executed, but it's a "Void" function (no outputs)
                        else
                        {
                            log($"✅ Function executed successfully.");
                        }
                    }
                }
                else
                {
                    log($"❌ Failed: {responseStr}");
                }
            }
            catch (Exception ex) { log($"[EXCEPTION] {ex.Message}"); }
        }

        private async Task FetchOutlinerActors()
        {
            var mapInfo = await GetCurrentMapInfo(_client);
            _mapNameInput.Text = mapInfo.ShortName;
            _log($"Fetching all actors from '{mapInfo.ShortName}'...");

            var actors = await GetActorsFromActiveWorld(_client, mapInfo.FullPath, "/Script/Engine.Actor");
            _actorDropdown.Items.Clear();
            string filterText = _actorFilterInput.Text.ToLower().Trim();

            var comboItems = new List<ComboBoxItem>();
            foreach (string path in actors)
            {
                string friendlyName = path.Split('.').Last();
                if (!string.IsNullOrEmpty(filterText) && !friendlyName.ToLower().Contains(filterText)) continue;
                comboItems.Add(new ComboBoxItem { Content = friendlyName, Tag = path });
            }

            int maxDisplay = 500;
            int totalFound = comboItems.Count;
            foreach (var item in comboItems.OrderBy(i => i.Content.ToString()).Take(maxDisplay)) _actorDropdown.Items.Add(item);

            if (totalFound > maxDisplay)
            {
                _actorDropdown.Items.Insert(0, new ComboBoxItem { Content = $"⚠️ Showing {maxDisplay} of {totalFound}", Tag = "", IsEnabled = false, FontWeight = FontWeights.Bold, Foreground = System.Windows.Media.Brushes.DarkRed });
                _actorDropdown.SelectedIndex = 1;
                _log($"[WARNING] Found {totalFound} actors. Showing top {maxDisplay}.");
            }
            else if (_actorDropdown.Items.Count > 0)
            {
                _actorDropdown.SelectedIndex = 0;
                _log($"[SUCCESS] Loaded {_actorDropdown.Items.Count} actors.");
            }
            else _log("[WARNING] No actors found.");
        }
        private async Task ConvertToDisplayNames()
        {
            if (_actorDropdown.Items.Count == 0) return;
            if (_actorDropdown.Items.Count > 100)
            {
                _log("[WARNING] Too many actors! Please filter to fewer than 100 items.");
                return;
            }

            _log($"Converting {_actorDropdown.Items.Count} items simultaneously...");
            string url = "http://localhost:30010/remote/object/call";

            var items = _actorDropdown.Items.Cast<ComboBoxItem>().ToList();
            var updateTasks = new List<Task>();
            int successCount = 0;

            // --- 1. START THE STOPWATCH ---
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            foreach (var item in items)
            {
                string objectPath = item.Tag?.ToString();

                updateTasks.Add(Task.Run(async () =>
                {
                    var payload = new
                    {
                        objectPath = "/Script/Engine.Default__KismetSystemLibrary",
                        functionName = "GetDisplayName",
                        parameters = new { Object = objectPath }
                    };

                    try
                    {
                        var response = await SendUnrealRequest(_client, url, payload);
                        if (response.IsSuccessStatusCode)
                        {
                            using (JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
                            {
                                string displayName = doc.RootElement.GetProperty("ReturnValue").GetString();
                                if (!string.IsNullOrEmpty(displayName))
                                {
                                    Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        item.Content = displayName;
                                        successCount++;
                                    });
                                }
                            }
                        }
                    }
                    catch { }
                }));
            }

            await Task.WhenAll(updateTasks);

            // --- 2. STOP THE STOPWATCH ---
            stopwatch.Stop();

            var sortedList = items.OrderBy(i => i.Content.ToString()).ToList();
            _actorDropdown.Items.Clear();
            foreach (var item in sortedList)
            {
                _actorDropdown.Items.Add(item);
            }

            if (_actorDropdown.Items.Count > 0) _actorDropdown.SelectedIndex = 0;

            // --- 3. LOG THE ELAPSED TIME ---
            _log($"[SUCCESS] Converted {successCount} names in {stopwatch.ElapsedMilliseconds} ms! Ready to inspect.");
        }
        //private async Task ConvertToDisplayNames()
        //{
        //    // --- 1. SAFETY CHECK ---
        //    if (_actorDropdown.Items.Count == 0) return;
        //    if (_actorDropdown.Items.Count > 100)
        //    {
        //        _log("[WARNING] Too many actors! Please filter the list to fewer than 100 items before converting.");
        //        return;
        //    }

        //    _log($"Converting {_actorDropdown.Items.Count} items to Display Names...");
        //    string url = "http://localhost:30010/remote/object/call";
        //    int successCount = 0;

        //    // 🟢 START THE STOPWATCH HERE
        //    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        //    // --- 2. LOOP AND FETCH NAMES ---
        //    foreach (ComboBoxItem item in _actorDropdown.Items)
        //    {
        //        var payload = new
        //        {
        //            objectPath = "/Script/Engine.Default__KismetSystemLibrary",
        //            functionName = "GetDisplayName",
        //            parameters = new { Object = item.Tag.ToString() }
        //        };

        //        try
        //        {
        //            var response = await SendUnrealRequest(_client, url, payload);
        //            if (response.IsSuccessStatusCode)
        //            {
        //                using (JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        //                {
        //                    string displayName = doc.RootElement.GetProperty("ReturnValue").GetString();
        //                    if (!string.IsNullOrEmpty(displayName))
        //                    {
        //                        item.Content = displayName; // Update the UI text!
        //                        successCount++;
        //                    }
        //                }
        //            }
        //        }
        //        catch { }
        //    }

        //    // --- 3. RE-SORT ALPHABETICALLY ---
        //    var sortedList = _actorDropdown.Items.Cast<ComboBoxItem>().OrderBy(i => i.Content.ToString()).ToList();
        //    _actorDropdown.Items.Clear();
        //    foreach (var item in sortedList)
        //    {
        //        _actorDropdown.Items.Add(item);
        //    }

        //    if (_actorDropdown.Items.Count > 0) _actorDropdown.SelectedIndex = 0;

        //    // 🟢 STOP THE STOPWATCH HERE
        //    stopwatch.Stop();

        //    // 🟢 LOG THE TIME IT TOOK
        //    _log($"[SUCCESS] Converted {successCount} names in {stopwatch.ElapsedMilliseconds} ms! Ready to inspect.");
        //}
        private async Task CheckSubLevels()
        {
            _log("Scanning for loaded Sub-Levels via Actor paths...");
            var mapInfo = await GetCurrentMapInfo(_client);

            // 1. Fetch all actors (This bypasses the private property block!)
            var actors = await GetActorsFromActiveWorld(_client, mapInfo.FullPath, "/Script/Engine.Actor");

            // Use a HashSet so we only keep unique level names (no duplicates)
            var subLevels = new HashSet<string>();

            // 2. Parse the addresses
            foreach (string path in actors)
            {
                // Path format: /Game/Path/Map.Map:LevelName.ActorName
                if (path.Contains(":"))
                {
                    string afterColon = path.Split(':').Last();  // "SubLevel_Lighting.PointLight_5"
                    string levelName = afterColon.Split('.').First(); // "SubLevel_Lighting"

                    // Ignore the main persistent level
                    if (levelName != "PersistentLevel")
                    {
                        subLevels.Add(levelName);
                    }
                }
            }

            // 3. Print the results
            if (subLevels.Count > 0)
            {
                _log($"[SUCCESS] Detected {subLevels.Count} active Sub-Level(s):");
                foreach (var lvl in subLevels)
                {
                    _log($"   -> {lvl}");
                }
            }
            else
            {
                _log("[INFO] No Sub-Levels detected. (Everything is in the PersistentLevel, or the map uses World Partition).");
            }
        }
        private string CleanUnrealTypeName(string rawType)
        {
            if (string.IsNullOrEmpty(rawType)) return "Unknown";
            return rawType.Replace("Property", "").Replace("Int32", "Int").Replace("FVector", "Vector").Replace("FRotator", "Rotator").Replace("FString", "String");
        }

        private async Task DiscoverFunctions()
        {
            if (_actorDropdown.SelectedItem is not ComboBoxItem selectedActor) return;

            _functionListBox.Items.Clear();
            _dynamicParamsPanel.Children.Clear();
            _inputControls.Clear();

            string url = "http://localhost:30010/remote/object/describe";
            var payload = new { objectPath = selectedActor.Tag.ToString() };

            try
            {
                var response = await SendUnrealRequest(_client, url, payload);
                if (response.IsSuccessStatusCode)
                {
                    using (JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
                    {
                        if (doc.RootElement.TryGetProperty("Functions", out JsonElement functionsArray))
                        {
                            string filterText = _functionFilterInput.Text.ToLower().Trim();
                            foreach (var func in functionsArray.EnumerateArray())
                            {
                                string funcName = func.GetProperty("Name").GetString();
                                if (!string.IsNullOrEmpty(filterText) && !funcName.ToLower().Contains(filterText)) continue;

                                var funcDef = new UFunctionDef { Name = funcName };
                                var displayArgs = new List<string>();

                                if (func.TryGetProperty("Arguments", out JsonElement argsArray))
                                {
                                    foreach (var arg in argsArray.EnumerateArray())
                                    {
                                        string argName = arg.GetProperty("Name").GetString();
                                        string argType = "Unknown";
                                        if (arg.TryGetProperty("Type", out JsonElement typeDirect)) argType = typeDirect.GetString();
                                        else if (arg.TryGetProperty("Property", out JsonElement prop))
                                        {
                                            if (prop.TryGetProperty("Type", out JsonElement t)) argType = t.GetString();
                                            else if (prop.TryGetProperty("FieldClass", out JsonElement fc)) argType = fc.GetString();
                                            else if (prop.TryGetProperty("CPPType", out JsonElement cpp)) argType = cpp.GetString();
                                        }
                                        argType = CleanUnrealTypeName(argType);
                                        funcDef.Parameters.Add(argName, argType);
                                        displayArgs.Add($"{argType} {argName}");
                                    }
                                }
                                funcDef.DisplayText = $"Function: {funcName}({string.Join(", ", displayArgs)})";
                                _functionListBox.Items.Add(funcDef);
                            }
                            _log($"[SUCCESS] Discovered {_functionListBox.Items.Count} functions.");
                        }
                    }
                }
            }
            catch { }
        }

        private void FunctionListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _dynamicParamsPanel.Children.Clear();
            _inputControls.Clear();
            if (_functionListBox.SelectedItem is not UFunctionDef selectedFunc) return;

            if (selectedFunc.Parameters.Count > 0)
            {
                _dynamicParamsPanel.Children.Add(new TextBlock { Text = "Function Parameters:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10) });
                foreach (var param in selectedFunc.Parameters)
                {
                    string pName = param.Key; string pType = param.Value.ToLower(); string pTypeDisplay = param.Value;
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
                    row.Children.Add(new TextBlock { Text = $"{pName} ({pTypeDisplay}):", Width = 150, VerticalAlignment = VerticalAlignment.Center });

                    if (pType == "bool" || pType == "boolean")
                    {
                        var cb = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
                        row.Children.Add(cb); _inputControls.Add(pName, cb);
                    }
                    else if (pType == "class" || pType == "softclass" || pType == "uclass")
                    {
                        var classPanel = new StackPanel { Orientation = Orientation.Horizontal };
                        var searchBox = new TextBox { Width = 70, Padding = new Thickness(3), Margin = new Thickness(0, 0, 5, 0), Text = "BP_" };
                        var searchBtn = new Button { Content = "🔍", Width = 30, Margin = new Thickness(0, 0, 5, 0) };
                        var classCombo = new ComboBox { Width = 140 };
                        searchBtn.Click += async (s, ev) => await PopulateClassDropdown(_client, searchBox.Text, classCombo, _log);
                        classPanel.Children.Add(searchBox); classPanel.Children.Add(searchBtn); classPanel.Children.Add(classCombo);
                        row.Children.Add(classPanel); _inputControls.Add(pName, classCombo);
                    }
                    else
                    {
                        var tb = new TextBox { Width = 250, Padding = new Thickness(3) };
                        row.Children.Add(tb); _inputControls.Add(pName, tb);
                    }
                    _dynamicParamsPanel.Children.Add(row);
                }
            }
            else _dynamicParamsPanel.Children.Add(new TextBlock { Text = "This function requires no parameters.", FontStyle = FontStyles.Italic, Foreground = System.Windows.Media.Brushes.Gray });
        }

        private async Task PopulateClassDropdown(HttpClient client, string query, ComboBox targetCombo, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(query)) return;
            targetCombo.Items.Clear(); targetCombo.Items.Add("Searching..."); targetCombo.SelectedIndex = 0;
            try
            {
                var response = await SendUnrealRequest(client, "http://localhost:30010/remote/search/assets", new { Query = query });
                if (response.IsSuccessStatusCode)
                {
                    using (JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
                    {
                        JsonElement assetsArray = doc.RootElement.TryGetProperty("Assets", out JsonElement inner) ? inner : doc.RootElement;
                        targetCombo.Items.Clear();
                        if (assetsArray.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var asset in assetsArray.EnumerateArray())
                            {
                                string name = "Unknown";
                                if (asset.TryGetProperty("Name", out JsonElement nElem)) name = nElem.GetString();
                                else if (asset.TryGetProperty("AssetName", out JsonElement anElem)) name = anElem.GetString();

                                string objectPath = string.Empty;
                                if (asset.TryGetProperty("ObjectPath", out JsonElement opElem)) objectPath = opElem.GetString();
                                else if (asset.TryGetProperty("Path", out JsonElement pElem)) objectPath = pElem.GetString();

                                string assetClass = string.Empty;
                                if (asset.TryGetProperty("Class", out JsonElement cElem)) assetClass = cElem.GetString();
                                else if (asset.TryGetProperty("AssetClass", out JsonElement acElem)) assetClass = acElem.GetString();
                                else if (asset.TryGetProperty("AssetClassPath", out JsonElement acpElem)) assetClass = acpElem.ToString();

                                if (!string.IsNullOrEmpty(objectPath) && !string.IsNullOrEmpty(assetClass) && assetClass.Contains("Blueprint"))
                                    targetCombo.Items.Add(new ComboBoxItem { Content = name, Tag = objectPath + "_C" });
                            }
                        }
                    }
                    if (targetCombo.Items.Count > 0) targetCombo.SelectedIndex = 0; else targetCombo.Items.Add("No Blueprints Found");
                }
            }
            catch { }
        }
    }
    // --- COMMAND 4: EDITOR CAMERA PRESETS (WITH LOCAL SAVING) ---
    public class SetEditorCameraAction : UnrealAction
    {
        private TextBox _presetNameInput;
        private ComboBox _presetDropdown;
        private TextBox _xLoc, _yLoc, _zLoc;
        private TextBox _pitchRot, _yawRot, _rollRot;

        // The path where our presets will be saved
        private readonly string _saveFilePath = "CameraPresets.json";
        private List<CameraPreset> _savedPresets = new List<CameraPreset>();

        // We make these properties public so the JSON Serializer can read/write them
        public class CameraPreset
        {
            public string Name { get; set; }
            public float X { get; set; }
            public float Y { get; set; }
            public float Z { get; set; }
            public float Pitch { get; set; }
            public float Yaw { get; set; }
            public float Roll { get; set; }
            public override string ToString() => Name;
        }

        public SetEditorCameraAction() { Name = "Editor Camera Presets"; }

        public override UIElement CreateUI(HttpClient client, Action<string> log)
        {
            var panel = new StackPanel { Orientation = Orientation.Vertical };

            // 1. Preset Management Row
            var topPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 15) };

            topPanel.Children.Add(new TextBlock { Text = "Preset Name:", Width = 80, VerticalAlignment = VerticalAlignment.Center });
            _presetNameInput = new TextBox { Width = 120, Padding = new Thickness(3), Margin = new Thickness(0, 0, 10, 0) };
            topPanel.Children.Add(_presetNameInput);

            var fetchBtn = new Button
            {
                Content = "Fetch & Save Current View",
                Padding = new Thickness(10, 5, 10, 5),
                Background = System.Windows.Media.Brushes.DarkGreen,
                Foreground = System.Windows.Media.Brushes.White,
                FontWeight = FontWeights.Bold
            };
            fetchBtn.Click += async (s, e) => await FetchAndSaveCamera(client, log);
            topPanel.Children.Add(fetchBtn);

            panel.Children.Add(topPanel);

            // 2. Preset Selection Row
            var dropPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 15) };
            dropPanel.Children.Add(new TextBlock { Text = "Saved Presets:", Width = 120, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold });

            _presetDropdown = new ComboBox { Width = 250 };
            _presetDropdown.SelectionChanged += PresetDropdown_SelectionChanged;
            dropPanel.Children.Add(_presetDropdown);

            // ---> NEW: The Delete Button <---
            var deleteBtn = new Button
            {
                Content = "Delete",
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(10, 0, 0, 0),
                Background = System.Windows.Media.Brushes.DarkRed,
                Foreground = System.Windows.Media.Brushes.White,
                FontWeight = FontWeights.Bold
            };
            deleteBtn.Click += (s, e) => DeleteSelectedPreset(log);
            dropPanel.Children.Add(deleteBtn);

            panel.Children.Add(dropPanel);

            // 3. Transform Inputs (Clean inline layout)
            panel.Children.Add(CreateTripleInputRow("Location:",
                "X:", out _xLoc, "0.0",
                "Y:", out _yLoc, "0.0",
                "Z:", out _zLoc, "0.0"));

            panel.Children.Add(CreateTripleInputRow("Rotation:",
                "Pitch:", out _pitchRot, "0.0",
                "Yaw:", out _yawRot, "0.0",
                "Roll:", out _rollRot, "0.0"));

            // NEW: Load existing presets from disk as soon as the UI is built
            LoadPresets(log);

            return panel;
        }

        // --- LOCAL FILE I/O ---
        private void LoadPresets(Action<string> log)
        {
            try
            {
                if (File.Exists(_saveFilePath))
                {
                    string json = File.ReadAllText(_saveFilePath);
                    _savedPresets = JsonSerializer.Deserialize<List<CameraPreset>>(json) ?? new List<CameraPreset>();

                    _presetDropdown.Items.Clear();
                    foreach (var preset in _savedPresets)
                    {
                        _presetDropdown.Items.Add(preset);
                    }

                    if (_presetDropdown.Items.Count > 0)
                    {
                        _presetDropdown.SelectedIndex = 0;
                        log($"[LOADED] Found {_savedPresets.Count} camera presets on disk.");
                    }
                }
            }
            catch (Exception ex)
            {
                log($"[WARNING] Could not load presets file: {ex.Message}");
            }
        }

        private void SavePresets(Action<string> log)
        {
            try
            {
                // WriteIndented makes the JSON file nicely formatted and readable if you open it in Notepad
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_savedPresets, options);
                File.WriteAllText(_saveFilePath, json);
            }
            catch (Exception ex)
            {
                log($"[ERROR] Could not save presets to disk: {ex.Message}");
            }
        }
        // --- NEW: DELETE PRESET LOGIC ---
        private void DeleteSelectedPreset(Action<string> log)
        {
            if (_presetDropdown.SelectedItem is CameraPreset presetToDelete)
            {
                // 1. Remove from our persistent list and the UI dropdown
                _savedPresets.Remove(presetToDelete);
                _presetDropdown.Items.Remove(presetToDelete);

                // 2. Save the updated (now smaller) list to the JSON file
                SavePresets(log);

                log($"[SUCCESS] Deleted camera preset: '{presetToDelete.Name}'");

                // 3. Clean up the UI
                if (_presetDropdown.Items.Count > 0)
                {
                    _presetDropdown.SelectedIndex = 0; // Auto-select the next available one
                }
                else
                {
                    // If we deleted the last one, reset the text boxes to 0
                    _xLoc.Text = "0.0"; _yLoc.Text = "0.0"; _zLoc.Text = "0.0";
                    _pitchRot.Text = "0.0"; _yawRot.Text = "0.0"; _rollRot.Text = "0.0";
                }
            }
            else
            {
                log("[WARNING] No preset selected to delete.");
            }
        }
        // --- FETCH AND SAVE CURRENT VIEW ---
        private async Task FetchAndSaveCamera(HttpClient client, Action<string> log)
        {
            log("Fetching current Editor Camera coordinates...");

            string url = "http://localhost:30010/remote/object/call";
            var payload = new
            {
                objectPath = "/Script/UnrealEd.Default__UnrealEditorSubsystem",
                functionName = "GetLevelViewportCameraInfo",
                generateTransaction = false
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            try
            {
                var response = await SendUnrealRequest(client, url, payload);
                //var response = await client.PutAsync(url, content);
                string responseStr = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    using (JsonDocument doc = JsonDocument.Parse(responseStr))
                    {
                        var root = doc.RootElement;

                        if (root.TryGetProperty("CameraLocation", out JsonElement loc) && root.TryGetProperty("CameraRotation", out JsonElement rot))
                        {
                            float x = (float)loc.GetProperty("X").GetDouble();
                            float y = (float)loc.GetProperty("Y").GetDouble();
                            float z = (float)loc.GetProperty("Z").GetDouble();

                            float pitch = (float)rot.GetProperty("Pitch").GetDouble();
                            float yaw = (float)rot.GetProperty("Yaw").GetDouble();
                            float roll = (float)rot.GetProperty("Roll").GetDouble();

                            string presetName = _presetNameInput.Text;
                            if (string.IsNullOrWhiteSpace(presetName))
                            {
                                presetName = $"View {DateTime.Now:HH:mm:ss}";
                            }

                            var newPreset = new CameraPreset
                            {
                                Name = presetName,
                                X = x,
                                Y = y,
                                Z = z,
                                Pitch = pitch,
                                Yaw = yaw,
                                Roll = roll
                            };

                            // Add to our persistent list and the UI
                            _savedPresets.Add(newPreset);
                            _presetDropdown.Items.Add(newPreset);
                            _presetDropdown.SelectedItem = newPreset;
                            _presetNameInput.Text = "";

                            // Save to disk immediately
                            SavePresets(log);

                            log($"[SUCCESS] Saved new camera preset: '{presetName}' to disk!");
                        }
                    }
                }
                else log($"[ERROR] Call rejected: {responseStr}");
            }
            catch (Exception ex) { log($"[EXCEPTION] Connection failed: {ex.Message}"); }
        }

        private void PresetDropdown_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_presetDropdown.SelectedItem is CameraPreset preset)
            {
                _xLoc.Text = preset.X.ToString("F2");
                _yLoc.Text = preset.Y.ToString("F2");
                _zLoc.Text = preset.Z.ToString("F2");

                _pitchRot.Text = preset.Pitch.ToString("F2");
                _yawRot.Text = preset.Yaw.ToString("F2");
                _rollRot.Text = preset.Roll.ToString("F2");
            }
        }

        // --- EXECUTE: APPLY THE VALUES TO THE VIEWPORT ---
        public override async Task ExecuteAsync(HttpClient client, Action<string> log)
        {
            if (!float.TryParse(_xLoc.Text, out float x) || !float.TryParse(_yLoc.Text, out float y) || !float.TryParse(_zLoc.Text, out float z) ||
                !float.TryParse(_pitchRot.Text, out float pitch) || !float.TryParse(_yawRot.Text, out float yaw) || !float.TryParse(_rollRot.Text, out float roll))
            {
                log("[ERROR] Invalid coordinates or rotation values.");
                return;
            }

            log($"Moving Editor Camera to X:{x} Y:{y} Z:{z}...");

            string url = "http://localhost:30010/remote/object/call";
            var payload = new
            {
                objectPath = "/Script/UnrealEd.Default__UnrealEditorSubsystem",
                functionName = "SetLevelViewportCameraInfo",
                parameters = new
                {
                    CameraLocation = new { X = x, Y = y, Z = z },
                    CameraRotation = new { Pitch = pitch, Yaw = yaw, Roll = roll }
                }
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            try
            {
                var response = await SendUnrealRequest(client, url, payload);
                //var response = await client.PutAsync(url, content);
                if (response.IsSuccessStatusCode) log($"[SUCCESS] Editor Camera moved!");
                else log($"[ERROR] Call rejected: {await response.Content.ReadAsStringAsync()}");
            }
            catch (Exception ex) { log($"[EXCEPTION] {ex.Message}"); }
        }
    }
    // --- COMMAND 5: SANITY CHECK / DIAGNOSTICS ---
    //public class SanityCheckAction : UnrealAction
    //{
    //    public SanityCheckAction() { Name = "⚙️ Diagnostics: Sanity Check"; }

    //    public override UIElement CreateUI(HttpClient client, Action<string> log)
    //    {
    //        var panel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(5) };

    //        var instructions = new TextBlock
    //        {
    //            Text = "Click 'Execute Selected Command' to test the Remote Control connection. This will verify if Unreal is responding to basic Editor queries.",
    //            TextWrapping = TextWrapping.Wrap,
    //            Foreground = System.Windows.Media.Brushes.LightGray,
    //            FontStyle = FontStyles.Italic
    //        };

    //        panel.Children.Add(instructions);
    //        return panel;
    //    }

    //    public override async Task ExecuteAsync(HttpClient client, Action<string> log)
    //    {
    //        log("--- STARTING SANITY CHECK ---");

    //        // Test 1: Project Name
    //        log("Testing Project Discovery...");
    //        string projName = await GetProjectModuleName(client);
    //        log($"=> Raw Project Name Returned: '{projName}'");

    //        // Test 2: Map Name
    //        log("Testing Map Discovery...");
    //        string mapName = await GetCurrentMapName(client);
    //        log($"=> Raw Map Name Returned: '{mapName}'");

    //        // Evaluation
    //        if (projName != "ProjectName" && mapName != "Level" && !string.IsNullOrWhiteSpace(mapName))
    //        {
    //            log("✅ SANITY CHECK PASSED! Unreal API is fully communicating.");
    //        }
    //        else
    //        {
    //            log("❌ SANITY CHECK WARNING: Unreal returned default fallback values. Ensure the Remote Control API plugin is enabled and running.");
    //        }

    //        log("-----------------------------");
    //    }
    //}

    // --- COMMAND 6: PLAYER TELEPORT & ROTATION PRESETS ---
    // --- COMMAND 6: PLAYER TELEPORT & ROTATION PRESETS (WITH DELETE) ---
    public class PlayerTeleportAction : UnrealAction
    {
        private TextBox _presetNameInput, _xLoc, _yLoc, _zLoc, _pitch, _yaw, _roll;
        private ComboBox _presetDropdown;
        private readonly string _saveFilePath = "PlayerPresets.json";
        private List<PlayerPreset> _savedPresets = new List<PlayerPreset>();

        public class PlayerPreset
        {
            public string Name { get; set; }
            public float X { get; set; }
            public float Y { get; set; }
            public float Z { get; set; }
            public float Pitch { get; set; }
            public float Yaw { get; set; }
            public float Roll { get; set; }
            public override string ToString() => Name;
        }

        public PlayerTeleportAction() { Name = "Player Teleport Presets"; }

        public override UIElement CreateUI(HttpClient client, Action<string> log)
        {
            var panel = new StackPanel { Orientation = Orientation.Vertical };

            // 1. Preset Management Row (Fetch & Save)
            var topPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 15) };
            topPanel.Children.Add(new TextBlock { Text = "Preset Name:", Width = 80, VerticalAlignment = VerticalAlignment.Center });
            _presetNameInput = new TextBox { Width = 120, Margin = new Thickness(0, 0, 10, 0) };
            topPanel.Children.Add(_presetNameInput);

            var fetchBtn = new Button
            {
                Content = "Fetch & Save Pos",
                Padding = new Thickness(10, 5, 10, 5),
                Background = System.Windows.Media.Brushes.DarkCyan,
                Foreground = System.Windows.Media.Brushes.White,
                FontWeight = FontWeights.Bold
            };
            fetchBtn.Click += async (s, e) => await FetchPlayerTransform(client, log);
            topPanel.Children.Add(fetchBtn);
            panel.Children.Add(topPanel);

            // 2. Dropdown Row (Select & Delete)
            var dropPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 15) };
            dropPanel.Children.Add(new TextBlock { Text = "Saved Points:", Width = 120, VerticalAlignment = VerticalAlignment.Center });

            _presetDropdown = new ComboBox { Width = 200 };
            _presetDropdown.SelectionChanged += (s, e) => {
                if (_presetDropdown.SelectedItem is PlayerPreset p)
                {
                    _xLoc.Text = p.X.ToString("F2"); _yLoc.Text = p.Y.ToString("F2"); _zLoc.Text = p.Z.ToString("F2");
                    _pitch.Text = p.Pitch.ToString("F2"); _yaw.Text = p.Yaw.ToString("F2"); _roll.Text = p.Roll.ToString("F2");
                }
            };
            dropPanel.Children.Add(_presetDropdown);

            // --- THE DELETE BUTTON ---
            var deleteBtn = new Button
            {
                Content = "Delete",
                Width = 60,
                Margin = new Thickness(10, 0, 0, 0),
                Background = System.Windows.Media.Brushes.DarkRed,
                Foreground = System.Windows.Media.Brushes.White,
                FontWeight = FontWeights.Bold
            };
            deleteBtn.Click += (s, e) => DeleteSelectedPreset(log);
            dropPanel.Children.Add(deleteBtn);

            panel.Children.Add(dropPanel);

            // 3. Coordinate Inputs
            panel.Children.Add(CreateTripleInputRow("Location:", "X:", out _xLoc, "0.0", "Y:", out _yLoc, "0.0", "Z:", out _zLoc, "0.0"));
            panel.Children.Add(CreateTripleInputRow("Rotation:", "P:", out _pitch, "0.0", "Y:", out _yaw, "0.0", "R:", out _roll, "0.0"));

            // Load Existing from Disk
            if (File.Exists(_saveFilePath))
            {
                try
                {
                    _savedPresets = JsonSerializer.Deserialize<List<PlayerPreset>>(File.ReadAllText(_saveFilePath)) ?? new List<PlayerPreset>();
                    foreach (var p in _savedPresets) _presetDropdown.Items.Add(p);
                    if (_presetDropdown.Items.Count > 0) _presetDropdown.SelectedIndex = 0;
                }
                catch { }
            }

            return panel;
        }

        private void DeleteSelectedPreset(Action<string> log)
        {
            if (_presetDropdown.SelectedItem is PlayerPreset p)
            {
                // 1. Remove from lists
                _savedPresets.Remove(p);
                _presetDropdown.Items.Remove(p);

                // 2. Save the now-shorter list to disk
                File.WriteAllText(_saveFilePath, JsonSerializer.Serialize(_savedPresets));
                log($"[SUCCESS] Deleted preset: {p.Name}");

                // 3. Handle UI cleanup
                if (_presetDropdown.Items.Count > 0)
                    _presetDropdown.SelectedIndex = 0;
                else
                {
                    _xLoc.Text = "0.0"; _yLoc.Text = "0.0"; _zLoc.Text = "0.0";
                    _pitch.Text = "0.0"; _yaw.Text = "0.0"; _roll.Text = "0.0";
                }
            }
            else
            {
                log("[WARNING] No preset selected to delete.");
            }
        }

        private async Task FetchPlayerTransform(HttpClient client, Action<string> log)
        {
            log("Locating Player Transform...");
            var mapInfo = await GetCurrentMapInfo(client);

            var getPawnPayload = new { objectPath = "/Script/Engine.Default__GameplayStatics", functionName = "GetPlayerPawn", parameters = new { WorldContextObject = mapInfo.FullPath, PlayerIndex = 0 } };
            var response = await client.PutAsync("http://localhost:30010/remote/object/call", new StringContent(JsonSerializer.Serialize(getPawnPayload), Encoding.UTF8, "application/json"));

            if (response.IsSuccessStatusCode)
            {
                using (JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
                {
                    string pawnPath = doc.RootElement.GetProperty("ReturnValue").GetString();

                    var locRes = await client.PutAsync("http://localhost:30010/remote/object/call", new StringContent(JsonSerializer.Serialize(new { objectPath = pawnPath, functionName = "K2_GetActorLocation" }), Encoding.UTF8, "application/json"));
                    var locJson = JsonDocument.Parse(await locRes.Content.ReadAsStringAsync()).RootElement.GetProperty("ReturnValue");

                    var rotRes = await client.PutAsync("http://localhost:30010/remote/object/call", new StringContent(JsonSerializer.Serialize(new { objectPath = pawnPath, functionName = "K2_GetActorRotation" }), Encoding.UTF8, "application/json"));
                    var rotJson = JsonDocument.Parse(await rotRes.Content.ReadAsStringAsync()).RootElement.GetProperty("ReturnValue");

                    var newP = new PlayerPreset
                    {
                        Name = string.IsNullOrWhiteSpace(_presetNameInput.Text) ? $"Point {DateTime.Now:HH:mm:ss}" : _presetNameInput.Text,
                        X = (float)locJson.GetProperty("X").GetDouble(),
                        Y = (float)locJson.GetProperty("Y").GetDouble(),
                        Z = (float)locJson.GetProperty("Z").GetDouble(),
                        Pitch = (float)rotJson.GetProperty("Pitch").GetDouble(),
                        Yaw = (float)rotJson.GetProperty("Yaw").GetDouble(),
                        Roll = (float)rotJson.GetProperty("Roll").GetDouble()
                    };

                    _savedPresets.Add(newP); _presetDropdown.Items.Add(newP); _presetDropdown.SelectedItem = newP;
                    File.WriteAllText(_saveFilePath, JsonSerializer.Serialize(_savedPresets));
                    log($"[SUCCESS] Saved Transform: {newP.Name}");
                }
            }
        }

        public override async Task ExecuteAsync(HttpClient client, Action<string> log)
        {
            try
            {
                var mapInfo = await GetCurrentMapInfo(client);
                var getPawnPayload = new { objectPath = "/Script/Engine.Default__GameplayStatics", functionName = "GetPlayerPawn", parameters = new { WorldContextObject = mapInfo.FullPath, PlayerIndex = 0 } };
                var pResponse = await client.PutAsync("http://localhost:30010/remote/object/call", new StringContent(JsonSerializer.Serialize(getPawnPayload), Encoding.UTF8, "application/json"));

                if (pResponse.IsSuccessStatusCode)
                {
                    string pawnPath = JsonDocument.Parse(await pResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("ReturnValue").GetString();

                    var telePayload = new
                    {
                        objectPath = pawnPath,
                        functionName = "K2_SetActorLocationAndRotation",
                        parameters = new
                        {
                            NewLocation = new { X = float.Parse(_xLoc.Text, System.Globalization.CultureInfo.InvariantCulture), Y = float.Parse(_yLoc.Text, System.Globalization.CultureInfo.InvariantCulture), Z = float.Parse(_zLoc.Text, System.Globalization.CultureInfo.InvariantCulture) },
                            NewRotation = new { Pitch = float.Parse(_pitch.Text, System.Globalization.CultureInfo.InvariantCulture), Yaw = float.Parse(_yaw.Text, System.Globalization.CultureInfo.InvariantCulture), Roll = float.Parse(_roll.Text, System.Globalization.CultureInfo.InvariantCulture) },
                            bSweep = false,
                            bTeleport = true
                        }
                    };

                    await client.PutAsync("http://localhost:30010/remote/object/call", new StringContent(JsonSerializer.Serialize(telePayload), Encoding.UTF8, "application/json"));
                    log("✅ Player Transform Updated!");
                }
            }
            catch (Exception ex) { log($"[ERROR] {ex.Message}"); }
        }
    }
    // ====================================================================
    // MACRO DATA & EXECUTION CLASSES
    // ====================================================================
    public class SavedMacro
    {
        public string Name { get; set; }
        public string ActorPath { get; set; }
        public string FunctionName { get; set; }

        // We save the types so we know whether to draw a TextBox or CheckBox
        public Dictionary<string, string> ParameterTypes { get; set; } = new Dictionary<string, string>();

        // We save the values you typed so they load as the default text!
        public Dictionary<string, string> DefaultValues { get; set; } = new Dictionary<string, string>();
    }

    public class SavedMacroAction : UnrealAction
    {
        private SavedMacro _macro;
        private Dictionary<string, Control> _inputControls = new Dictionary<string, Control>();

        public SavedMacroAction(SavedMacro macro)
        {
            _macro = macro;
            Name = "⚡ " + macro.Name; // The lightning bolt makes it stand out in the main list!
        }

        public override UIElement CreateUI(HttpClient client, Action<string> log)
        {
            var panel = new StackPanel { Orientation = Orientation.Vertical };

            // Header info
            panel.Children.Add(new TextBlock { Text = $"Target Actor: {_macro.ActorPath.Split('.').Last()}", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 5), Foreground = System.Windows.Media.Brushes.DarkCyan });
            panel.Children.Add(new TextBlock { Text = $"Function: {_macro.FunctionName}()", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 15), Foreground = System.Windows.Media.Brushes.DarkOrchid });

            _inputControls.Clear();

            // Auto-Generate Parameters UI
            if (_macro.ParameterTypes.Count > 0)
            {
                panel.Children.Add(new TextBlock { Text = "Parameters (Modify before running):", Margin = new Thickness(0, 0, 0, 10) });

                foreach (var kvp in _macro.ParameterTypes)
                {
                    string pName = kvp.Key;
                    string pType = kvp.Value.ToLower();
                    string defaultVal = _macro.DefaultValues.ContainsKey(pName) ? _macro.DefaultValues[pName] : "";

                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
                    row.Children.Add(new TextBlock { Text = $"{pName} ({pType}):", Width = 150, VerticalAlignment = VerticalAlignment.Center });

                    if (pType == "bool" || pType == "boolean")
                    {
                        var cb = new CheckBox { VerticalAlignment = VerticalAlignment.Center, IsChecked = (defaultVal.ToLower() == "true") };
                        row.Children.Add(cb); _inputControls.Add(pName, cb);
                    }
                    else
                    {
                        var tb = new TextBox { Width = 250, Padding = new Thickness(3), Text = defaultVal };
                        row.Children.Add(tb); _inputControls.Add(pName, tb);
                    }
                    panel.Children.Add(row);
                }
            }
            else
            {
                panel.Children.Add(new TextBlock { Text = "This function requires no parameters.", FontStyle = FontStyles.Italic, Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 0, 0, 15) });
            }

            // A delete button for the macro
            var deleteBtn = new Button { Content = "🗑️ Delete Command", Width = 150, Margin = new Thickness(0, 20, 0, 0), Background = System.Windows.Media.Brushes.DarkRed, Foreground = System.Windows.Media.Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
            deleteBtn.Click += (s, e) => {
                var macros = JsonSerializer.Deserialize<List<SavedMacro>>(File.ReadAllText("SavedMacros.json"));
                macros.RemoveAll(m => m.Name == _macro.Name);
                File.WriteAllText("SavedMacros.json", JsonSerializer.Serialize(macros));
                log($"[SUCCESS] Deleted macro '{_macro.Name}'");
                MainWindow.RefreshMacros?.Invoke(); // Tell the main window to update the side panel!
            };
            panel.Children.Add(deleteBtn);

            return panel;
        }

        //public override async Task ExecuteAsync(HttpClient client, Action<string> log)
        //{
        //    var dynamicParameters = new Dictionary<string, object>();

        //    foreach (var kvp in _macro.ParameterTypes)
        //    {
        //        string pName = kvp.Key;
        //        string pType = kvp.Value.ToLower();

        //        if (_inputControls.TryGetValue(pName, out Control uiControl))
        //        {
        //            try
        //            {
        //                if (uiControl is CheckBox cb) dynamicParameters.Add(pName, cb.IsChecked ?? false);
        //                else if (uiControl is TextBox tb)
        //                {
        //                    if (pType == "float" || pType == "double") dynamicParameters.Add(pName, float.Parse(tb.Text, System.Globalization.CultureInfo.InvariantCulture));
        //                    else if (pType == "int" || pType == "int64" || pType == "byte") dynamicParameters.Add(pName, int.Parse(tb.Text));
        //                    else if (pType == "bool" || pType == "boolean") dynamicParameters.Add(pName, bool.Parse(tb.Text.ToLower()));
        //                    else dynamicParameters.Add(pName, tb.Text);
        //                }
        //            }
        //            catch { log($"[ERROR] Invalid input for '{pName}'. Expected {pType}."); return; }
        //        }
        //    }

        //    string url = "http://localhost:30010/remote/object/call";
        //    var payload = new { objectPath = _macro.ActorPath, functionName = _macro.FunctionName, parameters = dynamicParameters, generateTransaction = true };

        //    try
        //    {
        //        var response = await SendUnrealRequest(client, url, payload);
        //        if (response.IsSuccessStatusCode) log($"✅ Executed {_macro.Name}!");
        //        else log($"❌ Failed: {await response.Content.ReadAsStringAsync()}");
        //    }
        //    catch (Exception ex) { log($"[EXCEPTION] {ex.Message}"); }
        //}
        public override async Task ExecuteAsync(HttpClient client, Action<string> log)
        {
            var dynamicParameters = new Dictionary<string, object>();

            foreach (var kvp in _macro.ParameterTypes)
            {
                string pName = kvp.Key;
                string pType = kvp.Value.ToLower();

                if (_inputControls.TryGetValue(pName, out Control uiControl))
                {
                    try
                    {
                        if (uiControl is CheckBox cb) dynamicParameters.Add(pName, cb.IsChecked ?? false);
                        else if (uiControl is TextBox tb)
                        {
                            if (pType == "float" || pType == "double") dynamicParameters.Add(pName, float.Parse(tb.Text, System.Globalization.CultureInfo.InvariantCulture));
                            else if (pType == "int" || pType == "int64" || pType == "byte") dynamicParameters.Add(pName, int.Parse(tb.Text));
                            else if (pType == "bool" || pType == "boolean") dynamicParameters.Add(pName, bool.Parse(tb.Text.ToLower()));
                            else dynamicParameters.Add(pName, tb.Text);
                        }
                    }
                    catch { log($"[ERROR] Invalid input for '{pName}'. Expected {pType}."); return; }
                }
            }

            // =======================================================
            // 1. DYNAMIC ACTOR RESOLUTION (The Sub-Level Fix)
            // =======================================================
            //string savedPath = _macro.ActorPath;

            //// Factor 1: The Actor Name (e.g., "NiagaraActor_0")
            //string targetActorName = savedPath.Split('.').Last();

            //// Factor 2: The Base Level Name (e.g., "LVL_I2_Thim")
            //// We split at the ':', take the left side, then split at the '.' and take the right side.
            //string baseLevelName = savedPath.Split(':')[0].Split('.').Last();

            //string resolvedPath = savedPath; // Fallback to old path just in case

            //try
            //{
            //    var mapInfo = await GetCurrentMapInfo(client);
            //    var currentActors = await GetActorsFromActiveWorld(client, mapInfo.FullPath, "/Script/Engine.Actor");

            //    // TWO-FACTOR SEARCH: Match the Actor Name AND the Level it belongs to!
            //    string foundPath = currentActors.FirstOrDefault(p =>
            //        p.EndsWith("." + targetActorName) &&
            //        p.Split(':')[0].EndsWith(baseLevelName)
            //    );

            //    if (!string.IsNullOrEmpty(foundPath))
            //    {
            //        resolvedPath = foundPath; // Success! Exact match found.
            //    }
            //    else
            //    {
            //        log($"[WARNING] '{targetActorName}' not found in '{baseLevelName}'. It might be unloaded. Trying fallback...");
            //    }
            //}
            //catch { /* If search fails, use fallback */ }

            //// =======================================================
            //// 2. EXECUTION & RETURN VALUE PARSING
            //// =======================================================
            //string url = "http://localhost:30010/remote/object/call";
            //var payload = new { objectPath = resolvedPath, functionName = _macro.FunctionName, parameters = dynamicParameters, generateTransaction = true };

            //try
            //{
            //    var response = await SendUnrealRequest(client, url, payload);
            //    string responseStr = await response.Content.ReadAsStringAsync();

            //    if (response.IsSuccessStatusCode)
            //    {
            //        using (JsonDocument doc = JsonDocument.Parse(responseStr))
            //        {
            //            var root = doc.RootElement;

            //            if (root.TryGetProperty("ReturnValue", out JsonElement retVal))
            //            {
            //                log($"✅ Executed {_macro.Name}! Result: {retVal.ToString()}");
            //            }
            //            else if (root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Any())
            //            {
            //                string prettyOutput = JsonSerializer.Serialize(JsonSerializer.Deserialize<object>(responseStr), new JsonSerializerOptions { WriteIndented = true });
            //                log($"✅ Executed {_macro.Name}! Outputs:\n{prettyOutput}");
            //            }
            //            else
            //            {
            //                log($"✅ {_macro.Name} executed successfully.");
            //            }
            //        }
            //    }
            //    else log($"❌ Failed: {responseStr}");
            //}
            //catch (Exception ex) { log($"[EXCEPTION] {ex.Message}"); }
            string savedPath = _macro.ActorPath;
            string resolvedPath = savedPath;

            // --- THE OPTIMIZATION: Check if we actually need a scan ---
            // If it's a LevelInstance OR we are currently in Play-In-Editor (UEDPIE), we need to scan.
            bool isDynamicPath = savedPath.Contains("LevelInstance") || savedPath.Contains("UEDPIE");

            if (isDynamicPath)
            {
                log("[DEBUG] Dynamic path detected. Scanning for fresh ID...");
                try
                {
                    string targetActorName = savedPath.Split('.').Last();
                    string baseLevelName = savedPath.Split(':')[0].Split('.').Last();

                    var mapInfo = await GetCurrentMapInfo(client);
                    var currentActors = await GetActorsFromActiveWorld(client, mapInfo.FullPath, "/Script/Engine.Actor");

                    string foundPath = currentActors.FirstOrDefault(p =>
                        p.EndsWith("." + targetActorName) &&
                        p.Split(':')[0].EndsWith(baseLevelName)
                    );

                    if (!string.IsNullOrEmpty(foundPath)) resolvedPath = foundPath;
                }
                catch { /* Fallback to savedPath if scan fails */ }
            }
            else
            {
                // FAST PATH: No scanning, just fire the request!
                // This takes < 1ms to decide.
            }

            // --- EXECUTION ---
            string url = "http://localhost:30010/remote/object/call";
            var payload = new { objectPath = resolvedPath, functionName = _macro.FunctionName, parameters = dynamicParameters, generateTransaction = true };

            try
            {
                var response = await SendUnrealRequest(client, url, payload);
                string responseStr = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    using (JsonDocument doc = JsonDocument.Parse(responseStr))
                    {
                        var root = doc.RootElement;
                        if (root.TryGetProperty("ReturnValue", out JsonElement retVal)) log($"✅ Result: {retVal}");
                        else log($"✅ Executed successfully.");
                    }
                }
                else log($"❌ Failed: {responseStr}");
            }
            catch (Exception ex) { log($"[EXCEPTION] {ex.Message}"); }
        }
    }
}