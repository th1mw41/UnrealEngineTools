using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;

namespace WpfApp2
{
    // THE VIEWMODEL
    public class CameraPresetMvvmViewModel : BaseViewModel
    {
        private readonly HttpClient _client;
        private readonly Action<string> _log;
        private readonly string _saveFilePath = "CameraPresets_MVVM.json";

        public ObservableCollection<CameraPresetMvvm> Presets { get; set; } = new ObservableCollection<CameraPresetMvvm>();

        private CameraPresetMvvm _selectedPreset;
        public CameraPresetMvvm SelectedPreset
        {
            get => _selectedPreset;
            set
            {
                _selectedPreset = value;
                OnPropertyChanged();
                if (_selectedPreset != null) LoadPresetIntoUI(_selectedPreset);
            }
        }

        private string _presetName;
        public string PresetName { get => _presetName; set { _presetName = value; OnPropertyChanged(); } }

        private float _x, _y, _z, _pitch, _yaw, _roll;
        public float X { get => _x; set { _x = value; OnPropertyChanged(); } }
        public float Y { get => _y; set { _y = value; OnPropertyChanged(); } }
        public float Z { get => _z; set { _z = value; OnPropertyChanged(); } }
        public float Pitch { get => _pitch; set { _pitch = value; OnPropertyChanged(); } }
        public float Yaw { get => _yaw; set { _yaw = value; OnPropertyChanged(); } }
        public float Roll { get => _roll; set { _roll = value; OnPropertyChanged(); } }

        public ICommand FetchCommand { get; }
        public ICommand ApplyCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand DeleteCommand { get; }
        public CameraPresetMvvmViewModel(HttpClient client, Action<string> log)
        {
            _client = client;
            _log = log;

            FetchCommand = new RelayCommand(async () => await FetchFromUnreal());
            ApplyCommand = new RelayCommand(async () => await ApplyToUnreal());
            SaveCommand = new RelayCommand(() => SaveToDisk());
            DeleteCommand = new RelayCommand(() => DeleteSelected());
            LoadFromDisk();
        }
        // 3. The Logic
        private void DeleteSelected()
        {
            if (SelectedPreset == null)
            {
                _log("⚠️ Select a preset to delete first.");
                return;
            }

            string nameToRemove = SelectedPreset.Name;

            // Remove from the ObservableCollection (UI updates automatically!)
            Presets.Remove(SelectedPreset);

            // Save the new, smaller list to the JSON file
            try
            {
                string json = JsonSerializer.Serialize(Presets, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_saveFilePath, json);
                _log($"🗑️ Deleted preset: '{nameToRemove}'");
                if (Presets.Count > 0)
                {
                    SelectedPreset = Presets[0];
                }
                else
                {
                    SelectedPreset = null; // List is totally empty
                }
                // Optional: Clear the input fields if the list is now empty
                if (Presets.Count == 0)
                {
                    PresetName = "";
                    X = Y = Z = Pitch = Yaw = Roll = 0;
                }
            }
            catch (Exception ex) { _log($"❌ Delete failed: {ex.Message}"); }
        }
        private void LoadPresetIntoUI(CameraPresetMvvm preset)
        {
            PresetName = preset.Name;
            X = preset.X; Y = preset.Y; Z = preset.Z;
            Pitch = preset.Pitch; Yaw = preset.Yaw; Roll = preset.Roll;
            _log($"[MVVM] Selected: {preset.Name}");
        }

        private async Task FetchFromUnreal()
        {
            _log("MVVM: Fetching current Editor Camera coordinates...");

            var payload = new
            {
                objectPath = "/Script/UnrealEd.Default__UnrealEditorSubsystem",
                functionName = "GetLevelViewportCameraInfo"
            };

            try
            {
                // 1. Serialize the payload
                string json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // --- THE FIX: Strip the charset header that causes the 400 error ---
                content.Headers.ContentType.CharSet = null;

                // 2. Send the request
                var response = await _client.PutAsync("http://localhost:30010/remote/object/call", content);
                string responseStr = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(responseStr);
                    var root = doc.RootElement;

                    // Update Properties
                    X = (float)root.GetProperty("CameraLocation").GetProperty("X").GetDouble();
                    Y = (float)root.GetProperty("CameraLocation").GetProperty("Y").GetDouble();
                    Z = (float)root.GetProperty("CameraLocation").GetProperty("Z").GetDouble();

                    Pitch = (float)root.GetProperty("CameraRotation").GetProperty("Pitch").GetDouble();
                    Yaw = (float)root.GetProperty("CameraRotation").GetProperty("Yaw").GetDouble();
                    Roll = (float)root.GetProperty("CameraRotation").GetProperty("Roll").GetDouble();

                    if (string.IsNullOrWhiteSpace(PresetName))
                        PresetName = $"View_{DateTime.Now:HH:mm:ss}";

                    _log("✅ Camera Data Fetched via MVVM!");
                }
                else
                {
                    _log($"❌ Unreal Error (400): {responseStr}");
                }
            }
            catch (Exception ex)
            {
                _log($"❌ Fetch Failed: {ex.Message}");
            }
        }

        private async Task ApplyToUnreal()
        {
            _log($"MVVM: Sending coordinates to Unreal...");

            // 1. Build the nested payload Unreal expects
            var payload = new
            {
                objectPath = "/Script/UnrealEd.Default__UnrealEditorSubsystem",
                functionName = "SetLevelViewportCameraInfo",
                parameters = new
                {
                    CameraLocation = new { X, Y, Z },
                    CameraRotation = new { Pitch, Yaw, Roll }
                }
            };

            try
            {
                // 2. Serialize and setup the content
                string json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // --- THE CRITICAL FIX ---
                // This removes the "; charset=utf-8" that causes Unreal to return a 400 error
                content.Headers.ContentType.CharSet = null;

                // 3. Fire the request
                var response = await _client.PutAsync("http://localhost:30010/remote/object/call", content);

                if (response.IsSuccessStatusCode)
                {
                    _log("✅ Unreal Camera Position Updated!");
                }
                else
                {
                    string errorResponse = await response.Content.ReadAsStringAsync();
                    _log($"❌ Unreal rejected Apply (400): {errorResponse}");
                }
            }
            catch (Exception ex)
            {
                _log($"❌ Apply Connection Error: {ex.Message}");
            }
        }

        private void SaveToDisk()
        {
            if (string.IsNullOrWhiteSpace(PresetName)) return;
            var newP = new CameraPresetMvvm { Name = PresetName, X = X, Y = Y, Z = Z, Pitch = Pitch, Yaw = Yaw, Roll = Roll };
            Presets.Add(newP);
            try
            {
                File.WriteAllText(_saveFilePath, JsonSerializer.Serialize(Presets, new JsonSerializerOptions { WriteIndented = true }));
                _log($"💾 Saved '{PresetName}'");
            }
            catch { _log("❌ Disk Save Failed."); }
        }

        private void LoadFromDisk()
        {
            if (!File.Exists(_saveFilePath)) return;
            try
            {
                var loaded = JsonSerializer.Deserialize<ObservableCollection<CameraPresetMvvm>>(File.ReadAllText(_saveFilePath));
                if (loaded != null)
                {
                    Presets.Clear();
                    foreach (var p in loaded) Presets.Add(p);
                    if (Presets.Count > 0)
                    {
                        SelectedPreset = Presets[0];
                    }
                }
            }
            catch { }
        }
    }

    // THE DATA MODEL (Moved here so it's guaranteed to be found)
    public class CameraPresetMvvm
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
}