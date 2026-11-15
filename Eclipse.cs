// Eclipse — éteint les écrans sans mettre le PC en veille, bloque le clavier et la souris, coupe le son,
// et règle la luminosité des écrans, jusque sous leur minimum.
//
//   Ctrl+Alt+Maj+E            éteint, puis rallume (le raccourci se change depuis le menu de l'icône)
//   Ctrl+Alt+Maj+Échap        sortie de secours : tout revient, tout de suite
//   Clic sur l'icône          le panneau de luminosité (un curseur par écran)
//   Double-clic sur l'icône   éteint
//   Ctrl+Alt+Maj+Haut / Bas   luminosité +10 % / -10 % sur tous les écrans
//
// Un seul fichier, compilé par le compilateur du .NET Framework 4, déjà présent dans Windows : voir Compiler.bat.
// Eclipse n'écrit qu'à trois endroits : %LOCALAPPDATA%\Eclipse (journal, sorties rendues muettes), la clé
// HKCU\Software\Eclipse (raccourci, « Lier les écrans ») et, si vous le cochez, le lancement au démarrage
// (HKCU\...\Run). Rien n'est envoyé sur Internet ; le seul échange réseau est local, avec SteelSeries Sonar s'il
// est installé.
//
// Comment c'est construit (le détail est dans les commentaires de chaque classe) :
// - chaque tâche qui peut traîner a son propre fil : la fenêtre noire (Overlay), le blocage du clavier et de la
//   souris (InputBlocker), le son et Sonar (Worker), la garde (Watchdog), la luminosité (Brightness). Au rallumage,
//   la fenêtre part d'abord et le clavier et la souris reviennent aussitôt (Rallumage) ; viennent ensuite les écrans,
//   le son et Sonar, et une étape qui échoue n'arrête pas les suivantes ;
// - le son : un simple « muet » des sorties par défaut, noté dans un fichier pour être retiré même après un arrêt
//   brutal ; aucun volume n'est changé (Sound). Avec SteelSeries Sonar, ses sorties sont notées avant d'éteindre et
//   remises au rallumage (Sonar) ;
// - la luminosité : DDC/CI, la norme par laquelle le PC parle aux réglages de l'écran (Brightness, BrightnessPanel,
//   Slider, LightOsd) ; sous le minimum, l'image elle-même est assombrie par l'effet de couleur de Windows (Dimmer) ;
// - pas de fenêtre d'erreur cachée sous l'écran noir : tout va dans %LOCALAPPDATA%\Eclipse\journal.txt (Journal).
//
// Historique : CHANGELOG.md. Licence : MIT (LICENSE).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace Eclipse
{
    static class Native
    {
        public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const int WM_SYSCOMMAND = 0x0112;
        public const int SC_MONITORPOWER = 0xF170;
        public const int WM_HOTKEY = 0x0312;
        public const int WM_POWERBROADCAST = 0x0218;
        public const int PBT_POWERSETTINGCHANGE = 0x8013;
        public const int WM_ENDSESSION = 0x0016;
        public const int WM_APP_RELEASE = 0x8001;       // au fil principal : finir le rallumage
        public const int WM_APP_BEGIN_RELEASE = 0x8002; // au fil du blocage : rendre le clavier et la souris
        public const int WM_APP_LIGHT = 0x8003;         // au fil principal : afficher la luminosité réglée au clavier
        public const uint VK_UP = 0x26, VK_DOWN = 0x28;
        public const int WM_QUIT = 0x0012;
        public const int WM_TIMER = 0x0113;
        public const int SW_HIDE = 0;
        public const uint ES_CONTINUOUS = 0x80000000;
        public const uint ES_SYSTEM_REQUIRED = 0x00000001;
        public const uint ES_DISPLAY_REQUIRED = 0x00000002;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_NOREPEAT = 0x4000;
        public const uint VK_ESCAPE = 0x1B;

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }

        [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
        [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] public static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint threadId, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr fn);
        [DllImport("user32.dll")] public static extern bool KillTimer(IntPtr hwnd, IntPtr id);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] public static extern IntPtr RegisterPowerSettingNotification(IntPtr hwnd, ref Guid setting, int flags);
        [DllImport("user32.dll")] public static extern bool UnregisterPowerSettingNotification(IntPtr handle);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hwnd, int cmd); // sans attendre le fil de la fenêtre
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr cursor);
        [DllImport("user32.dll")] public static extern bool LockWorkStation();
        [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
        [DllImport("user32.dll")] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr desk);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetUserObjectInformation(IntPtr obj, int index, StringBuilder info, int length, out int needed);
        [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfo")] public static extern bool SystemParametersInfoSet(uint action, uint param, IntPtr value, uint winIni);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);

        // Le bureau de Windows qui reçoit le clavier est-il le bureau normal ? Non pendant Ctrl+Alt+Suppr, le verrou, ou une
        // fenêtre d'administrateur (bureau sécurisé).
        public static bool OnNormalDesktop()
        {
            IntPtr desk = OpenInputDesktop(0, false, 1); // DESKTOP_READOBJECTS
            if (desk == IntPtr.Zero) return false;
            try
            {
                var name = new StringBuilder(64);
                int needed;
                if (!GetUserObjectInformation(desk, 2, name, name.Capacity * 2, out needed)) return true; // UOI_NAME
                return string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
            }
            finally { CloseDesktop(desk); }
        }
    }

    // ---- Le son de Windows (Core Audio) ----

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        void Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        void OpenPropertyStore(int access, out IntPtr store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out int state);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr notify);
        void UnregisterControlChangeNotify(IntPtr notify);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float level, ref Guid context);
        void SetMasterVolumeLevelScalar(float level, ref Guid context);
        void GetMasterVolumeLevel(out float level);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        void GetChannelVolumeLevel(uint channel, out float level);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    // Le « muet » : celui de la touche muet du clavier, posé sur les sorties par défaut de Windows (celles des trois
    // usages : système, multimédia, communications ; avec Sonar, ce sont ses sorties virtuelles « Gaming » et « Chat »).
    // Jamais de changement de sortie ni de volume. Les sorties qu'on a rendues muettes sont notées dans un fichier, pour
    // retirer le muet même après un arrêt brutal ; une sortie que vous aviez déjà rendue muette n'est pas notée, elle
    // reste muette au retour.
    static class Sound
    {
        const int ActiveState = 1;
        const int NotFound = unchecked((int)0x80070490);
        static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Eclipse");
        static readonly string StateFile = Path.Combine(Folder, "muet.txt");
        static readonly string OldStateFile = Path.Combine(Folder, "volume.txt"); // celui de l'ancienne version

        static IAudioEndpointVolume Open(IMMDevice device)
        {
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            object result;
            device.Activate(ref iid, 23, IntPtr.Zero, out result); // CLSCTX_ALL
            return (IAudioEndpointVolume)result;
        }

        static List<string> ReadState()
        {
            var ids = new List<string>();
            try
            {
                if (File.Exists(StateFile))
                    foreach (string line in File.ReadAllLines(StateFile))
                        if (line.Trim().Length > 0 && !ids.Contains(line.Trim())) ids.Add(line.Trim());
            }
            catch { }
            return ids;
        }

        static void WriteState(List<string> ids)
        {
            if (ids.Count == 0) { if (File.Exists(StateFile)) File.Delete(StateFile); return; }
            Directory.CreateDirectory(Folder);
            File.WriteAllLines(StateFile, ids.ToArray());
        }

        public static void Mute()
        {
            try
            {
                Restore(); // un muet resté d'une fois précédente (arrêt brutal) est d'abord retiré
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                var toMute = new List<KeyValuePair<string, IAudioEndpointVolume>>();
                var seen = new HashSet<string>();
                for (int role = 0; role < 3; role++)
                {
                    try
                    {
                        IMMDevice device;
                        enumerator.GetDefaultAudioEndpoint(0, role, out device);
                        string id;
                        device.GetId(out id);
                        if (!seen.Add(id)) continue;
                        var volume = Open(device);
                        bool muted;
                        volume.GetMute(out muted);
                        if (!muted) toMute.Add(new KeyValuePair<string, IAudioEndpointVolume>(id, volume));
                    }
                    catch { }
                }
                // Noter d'abord, couper ensuite : jamais un muet sans sa trace.
                var ids = ReadState();
                foreach (var pair in toMute) if (!ids.Contains(pair.Key)) ids.Add(pair.Key);
                WriteState(ids);
                Guid context = Guid.Empty;
                foreach (var pair in toMute)
                    try { pair.Value.SetMute(true, ref context); } catch { }
            }
            catch { }
        }

        // Retire le muet posé par Eclipse. Vrai quand il ne reste rien à faire ; faux s'il faut réessayer (une sortie
        // pas encore revenue, par exemple).
        public static bool Restore()
        {
            bool done = RestoreOldVersion();
            var ids = ReadState();
            if (ids.Count == 0) return done;
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                var left = new List<string>();
                Guid context = Guid.Empty;
                foreach (string id in ids)
                {
                    try
                    {
                        IMMDevice device;
                        enumerator.GetDevice(id, out device);
                        int state;
                        device.GetState(out state);
                        if (state != ActiveState) { left.Add(id); continue; }
                        var volume = Open(device);
                        volume.SetMute(false, ref context);
                        bool muted;
                        volume.GetMute(out muted);
                        if (muted) left.Add(id);
                    }
                    catch (COMException e)
                    {
                        if (e.ErrorCode != NotFound) left.Add(id); // une sortie qui n'existe plus : rien à remettre
                    }
                    catch { left.Add(id); }
                }
                WriteState(left);
                return done && left.Count == 0;
            }
            catch { return false; }
        }

        // L'ancienne version mettait les volumes à zéro et notait ceux d'avant dans volume.txt. S'il en reste un, on
        // remet ces volumes une fois (jamais un volume nul), sans toucher à la sortie par défaut, puis on l'efface.
        static bool RestoreOldVersion()
        {
            try
            {
                if (!File.Exists(OldStateFile)) return true;
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                var left = new List<string>();
                Guid context = Guid.Empty;
                foreach (string line in File.ReadAllLines(OldStateFile))
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0 || line[0] == 'D') continue;
                    try
                    {
                        float level = float.Parse(line.Substring(0, tab), System.Globalization.CultureInfo.InvariantCulture);
                        if (level < 0.01f) continue;
                        IMMDevice device;
                        enumerator.GetDevice(line.Substring(tab + 1), out device);
                        int state;
                        device.GetState(out state);
                        if (state != ActiveState) { left.Add(line); continue; }
                        Open(device).SetMasterVolumeLevelScalar(level, ref context);
                    }
                    catch (COMException e) { if (e.ErrorCode != NotFound) left.Add(line); }
                    catch { }
                }
                if (left.Count == 0) { File.Delete(OldStateFile); return true; }
                File.WriteAllLines(OldStateFile, left.ToArray());
                return false;
            }
            catch { return false; }
        }
    }

    // ---- SteelSeries Sonar : remettre ses sorties au rallumage ----

    // Essai du 7/11 : quand les écrans s'éteignent, Windows retire leurs sorties son (celles des écrans en HDMI ou DisplayPort) ; au
    // rallumage, un écran revient avant l'autre et Sonar bascule dessus. Le muet n'y est pour rien. Avant d'éteindre, on note donc
    // la sortie de chaque canal de Sonar (Game, Chat, Media, Aux) ; au rallumage, pendant 90 s (les écrans vont et
    // viennent quelques secondes), on remet tout canal qui a changé, dès que sa sortie est revenue. On passe par le
    // petit serveur local de Sonar, celui qu'utilise l'application SteelSeries GG ; son adresse est lue dans
    // C:\ProgramData\SteelSeries\GG\coreProps.json. Sans Sonar, rien ne se passe.
    static class Sonar
    {
        static readonly string[] Channels = { "game", "chat", "media", "aux" };
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        static Dictionary<string, string> savedIds;   // canal -> identifiant de la sortie
        static Dictionary<string, string> savedNames; // identifiant -> nom (« 5 - MonEcran (AMD High Definition Audio Device) »)
        static string baseUrl;

        static Sonar()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
            // Le serveur de GG, sur la machine même, a son propre certificat : accepté pour 127.0.0.1 seulement.
            ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, errors) =>
                errors == SslPolicyErrors.None || (sender is HttpWebRequest && ((HttpWebRequest)sender).RequestUri.IsLoopback);
        }

        public static bool Pending { get { return savedIds != null; } }
        public static void Forget() { savedIds = null; savedNames = null; }

        static string Call(string method, string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.Proxy = null;
            request.Timeout = request.ReadWriteTimeout = 1500;
            if (method == "PUT") request.ContentLength = 0;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                return reader.ReadToEnd();
        }

        static object Get(string path) { return Json.DeserializeObject(Call("GET", baseUrl + path)); }

        static string FindSonar()
        {
            string props = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"SteelSeries\GG\coreProps.json");
            if (!File.Exists(props)) return null;
            var core = Json.DeserializeObject(File.ReadAllText(props)) as Dictionary<string, object>;
            object gg;
            if (core == null || !core.TryGetValue("ggEncryptedAddress", out gg)) return null;
            var apps = Json.DeserializeObject(Call("GET", "https://" + gg + "/subApps")) as Dictionary<string, object>;
            var sonar = Field(Field(Field(apps, "subApps"), "sonar"), "metadata");
            object url;
            if (sonar == null || !sonar.TryGetValue("webServerAddress", out url) || string.IsNullOrEmpty(url as string)) return null;
            return ((string)url).TrimEnd('/');
        }

        static Dictionary<string, object> Field(Dictionary<string, object> obj, string name)
        {
            object value;
            return obj != null && obj.TryGetValue(name, out value) ? value as Dictionary<string, object> : null;
        }

        static string Text(Dictionary<string, object> obj, string name)
        {
            object value;
            return obj != null && obj.TryGetValue(name, out value) ? value as string ?? string.Empty : string.Empty;
        }

        static Dictionary<string, string> Redirections()
        {
            var result = new Dictionary<string, string>();
            var list = Get("/classicRedirections") as object[];
            if (list != null)
                foreach (object item in list)
                {
                    var r = item as Dictionary<string, object>;
                    if (r != null) result[Text(r, "id")] = Text(r, "deviceId");
                }
            return result;
        }

        // Les sorties actives que Sonar connaît : identifiant -> nom.
        static Dictionary<string, string> ActiveOutputs()
        {
            var result = new Dictionary<string, string>();
            var list = Get("/audioDevices") as object[];
            if (list != null)
                foreach (object item in list)
                {
                    var d = item as Dictionary<string, object>;
                    if (d != null && Text(d, "dataFlow") == "render" && Text(d, "state") == "active") result[Text(d, "id")] = Text(d, "friendlyName");
                }
            return result;
        }

        // « 5 - MonEcran (AMD ...) » -> « MonEcran (AMD ...) » : Windows renumérote parfois l'écran (1, 3, 5...).
        static string WithoutNumber(string name) { return Regex.Replace(name ?? string.Empty, @"^\d+\s*-\s*", string.Empty); }

        // Avant d'éteindre : la sortie de chaque canal.
        public static void Snapshot()
        {
            Forget();
            try
            {
                baseUrl = FindSonar();
                if (baseUrl == null || (Get("/mode/") as string) != "classic") return;
                var outputs = ActiveOutputs();
                var ids = new Dictionary<string, string>();
                var names = new Dictionary<string, string>();
                foreach (var pair in Redirections())
                {
                    string name;
                    if (Array.IndexOf(Channels, pair.Key) < 0 || pair.Value.Length == 0 || !outputs.TryGetValue(pair.Value, out name)) continue;
                    ids[pair.Key] = pair.Value;
                    names[pair.Value] = name;
                }
                if (ids.Count > 0) { savedIds = ids; savedNames = names; }
            }
            catch { Forget(); }
        }

        // Au rallumage : remet chaque canal qui a changé, si sa sortie est revenue. Vrai quand tout est en place.
        public static bool Restore()
        {
            if (savedIds == null) return true;
            try
            {
                Dictionary<string, string> current, outputs;
                try { current = Redirections(); outputs = ActiveOutputs(); }
                catch (WebException)
                {
                    baseUrl = FindSonar(); // Sonar a redémarré : nouvelle adresse
                    if (baseUrl == null) return false;
                    current = Redirections();
                    outputs = ActiveOutputs();
                }
                bool inPlace = true;
                foreach (var pair in savedIds)
                {
                    string target = pair.Value;
                    if (!outputs.ContainsKey(target))
                    {
                        // La même sortie revenue sous un autre numéro ; sinon, elle n'est pas encore là.
                        string wanted = WithoutNumber(savedNames[pair.Value]);
                        target = null;
                        foreach (var output in outputs)
                            if (WithoutNumber(output.Value) == wanted) { target = output.Key; break; }
                        if (target == null) { inPlace = false; continue; }
                    }
                    string now;
                    if (current.TryGetValue(pair.Key, out now) && now == target) continue;
                    Call("PUT", baseUrl + "/classicRedirections/" + pair.Key + "/deviceId/" + Uri.EscapeDataString(target));
                    inPlace = false; // revérifié au tour suivant
                }
                return inPlace;
            }
            catch { return false; }
        }
    }

    // ---- Le journal : %LOCALAPPDATA%\Eclipse\journal.txt (extinctions, rallumages, erreurs) ----

    static class Journal
    {
        static readonly object Gate = new object();
        static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Eclipse");
        static readonly string FilePath = Path.Combine(Folder, "journal.txt");
        static readonly string OldPath = Path.Combine(Folder, "journal.ancien.txt");

        // Jamais depuis le blocage du clavier : écrire un fichier est trop lent pour lui.
        public static void Write(string text)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Folder);
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > 200000)
                    {
                        if (File.Exists(OldPath)) File.Delete(OldPath);
                        File.Move(FilePath, OldPath);
                    }
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + text + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    // ---- Le fil du son ----

    // Le muet passe par le service du son de Windows, Sonar par son petit serveur ; tous deux peuvent tarder, surtout au
    // rallumage, quand les sorties son des écrans reviennent. Ils ont donc leur propre fil, un travail après l'autre :
    // jamais celui de la fenêtre noire ni celui de l'icône.
    class Worker
    {
        readonly Queue<Action> queue = new Queue<Action>();
        readonly AutoResetEvent signal = new AutoResetEvent(false);

        public Worker()
        {
            var thread = new Thread(Run) { IsBackground = true, Name = "Son" };
            thread.Start();
        }

        public void Post(Action job)
        {
            lock (queue) queue.Enqueue(job);
            signal.Set();
        }

        // Fait le travail et attend sa fin, au plus timeoutMs. Faux s'il n'a pas fini à temps (il finira plus tard).
        public bool RunAndWait(Action job, int timeoutMs)
        {
            var done = new ManualResetEvent(false);
            Post(delegate { try { job(); } finally { done.Set(); } });
            return done.WaitOne(timeoutMs);
        }

        public bool HasPending { get { lock (queue) return queue.Count > 0; } }

        // Attend ms millisecondes ; vrai si un autre travail arrive entre-temps (il passe alors avant).
        public bool Pause(int ms) { return signal.WaitOne(ms) || HasPending; }

        void Run()
        {
            while (true)
            {
                Action job = null;
                lock (queue) if (queue.Count > 0) job = queue.Dequeue();
                if (job == null) { signal.WaitOne(); continue; }
                try { job(); }
                catch (Exception e) { Journal.Write("Son : " + e.Message); }
            }
        }
    }

    // ---- Le rallumage, partagé entre les fils ----

    // Qui que ce soit qui rallume (le blocage qui voit le raccourci, Windows, le fil de garde), et de n'importe quel fil :
    // la fenêtre part d'abord, le clavier et la souris reviennent aussitôt, et le fil principal fait le reste (écrans,
    // son, Sonar). Rien ici n'attend un autre fil : tout est posté.
    static class Rallumage
    {
        public static IntPtr OverlayHwnd, AppHwnd; // posés au démarrage, avant toute extinction
        public static volatile bool Active;        // une extinction est en cours
        public static volatile bool UiDone;        // le fil principal a fini le rallumage
        public static volatile bool Wake, ByYou;
        public static volatile string Why = string.Empty;
        public static volatile int StartTick, RequestTick, EmergencyTick;
        public static volatile bool Emergency;
        public static volatile bool ScreenSaverWasOn;
        public static volatile InputBlocker Blocker;
        static int requested;

        public static bool Requested { get { return Thread.VolatileRead(ref requested) != 0; } }
        public static bool OverlayVisible { get { return OverlayHwnd != IntPtr.Zero && Native.IsWindowVisible(OverlayHwnd); } }

        public static void Begin()
        {
            Interlocked.Exchange(ref requested, 0);
            Emergency = UiDone = false;
            Blocker = null;
            Why = string.Empty;
            StartTick = Environment.TickCount;
            Active = true;
        }

        // wake : rallumer les écrans. byYou : c'est vous qui rallumez ; seulement alors la souris est bougée d'un pixel
        // pour réveiller les écrans, un geste que Windows et les autres programmes comptent comme votre retour.
        public static bool Request(bool wake, bool byYou, string why)
        {
            if (!Active || Interlocked.CompareExchange(ref requested, 1, 0) != 0) return false;
            Wake = wake;
            ByYou = byYou;
            Why = why;
            RequestTick = Environment.TickCount;
            Native.ShowWindowAsync(OverlayHwnd, Native.SW_HIDE); // la fenêtre d'abord, sans attendre son fil
            InputBlocker b = Blocker;
            if (b != null) b.BeginRelease();                     // puis le clavier et la souris
            Native.PostMessage(AppHwnd, Native.WM_APP_RELEASE, IntPtr.Zero, IntPtr.Zero); // le reste, au fil principal
            return true;
        }

        // La sortie de secours : comme le raccourci, mais le clavier et la souris sont rendus sans attendre qu'on relâche
        // les touches, et le fil de garde ferme Eclipse de force si la fenêtre n'est pas partie 1,5 s après.
        public static void EmergencyExit(string why)
        {
            if (!Request(true, true, why))
            {
                Native.ShowWindowAsync(OverlayHwnd, Native.SW_HIDE);
                Native.PostMessage(AppHwnd, Native.WM_APP_RELEASE, IntPtr.Zero, IntPtr.Zero);
            }
            InputBlocker b = Blocker;
            if (b != null) b.ForceAll();
            if (!Emergency) { EmergencyTick = Environment.TickCount; Emergency = true; }
        }

        // Rallumer les écrans depuis un autre fil que le fil principal (quand il ne répond plus).
        public static void WakeScreens(bool nudgeMouse)
        {
            Native.SetThreadExecutionState(Native.ES_DISPLAY_REQUIRED);
            if (!nudgeMouse) return;
            Native.mouse_event(1, 1, 0, 0, IntPtr.Zero);  // MOUSEEVENTF_MOVE : un pixel à droite...
            Native.mouse_event(1, -1, 0, 0, IntPtr.Zero); // ... et retour
        }
    }

    // ---- Le blocage du clavier et de la souris (crochets bas niveau, sur leur propre fil) ----

    // Ce fil ne fait que ça. Il voit chaque touche et chaque clic avant Windows, reconnaît lui-même le raccourci et la
    // sortie de secours, cache la fenêtre (par Rallumage) et se retire seul : rien n'y dépend du fil principal.
    class InputBlocker
    {
        const int EscHoldMs = 10000;
        const int ReleaseWaitMs = 5000;
        const uint AllMods = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT;
        readonly uint comboMods, comboKey;
        readonly Native.HookProc kbProc, msProc; // gardés en champ : le ramasse-miettes ne doit pas les libérer
        readonly HashSet<uint> keysDown = new HashSet<uint>();
        readonly ManualResetEvent started = new ManualResetEvent(false);
        IntPtr kbHook, msHook;
        Thread thread;
        volatile uint threadId;
        bool installed;
        int mouseMask;
        volatile int held;
        volatile int escStart;
        volatile bool escHeld;
        volatile bool releasing, forceAll;
        volatile int releaseStart;

        public InputBlocker(uint comboMods, uint comboKey)
        {
            this.comboMods = comboMods;
            this.comboKey = comboKey;
            kbProc = KeyboardProc;
            msProc = MouseProc;
        }

        public bool EscHeldLongEnough { get { return escHeld && !releasing && Environment.TickCount - escStart >= EscHoldMs; } }
        public bool Alive { get { Thread t = thread; return t != null && t.IsAlive; } }
        public bool Releasing { get { return releasing; } }
        public int ReleaseStart { get { return releaseStart; } }

        public bool Start()
        {
            thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "Blocage" };
            thread.Start();
            started.WaitOne(3000);
            if (!installed) { Stop(); return false; }
            return true;
        }

        // Rendre le clavier et la souris, de n'importe quel fil : tout ce qui est nouveau passe aussitôt ; seules les touches
        // et les boutons encore enfoncés restent retenus jusqu'à ce qu'on les relâche (5 s au plus), pour qu'une touche du
        // raccourci n'arrive pas seule dans le programme du dessous. Puis le fil retire ses crochets et s'arrête.
        public void BeginRelease()
        {
            if (releasing) return;
            releaseStart = Environment.TickCount;
            releasing = true;
            Native.PostThreadMessage(threadId, Native.WM_APP_BEGIN_RELEASE, IntPtr.Zero, IntPtr.Zero);
        }

        // Tout rendre, tout de suite.
        public void ForceAll()
        {
            forceAll = true;
            BeginRelease();
            Native.PostThreadMessage(threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        public void Stop()
        {
            Thread t = thread;
            if (t == null) return;
            ForceAll();
            t.Join(1000);
        }

        void Run()
        {
            Native.MSG msg;
            Native.PeekMessage(out msg, IntPtr.Zero, 0x400, 0x400, 0); // crée la file de messages du fil
            threadId = Native.GetCurrentThreadId();
            installed = Install();
            started.Set();
            if (installed)
            {
                // Windows retire en silence un crochet trop lent : on le repose toutes les 30 s.
                IntPtr reinstall = Native.SetTimer(IntPtr.Zero, IntPtr.Zero, 30000, IntPtr.Zero);
                IntPtr check = IntPtr.Zero;
                while (!forceAll && Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.message == Native.WM_APP_BEGIN_RELEASE)
                    {
                        if (check == IntPtr.Zero) check = Native.SetTimer(IntPtr.Zero, IntPtr.Zero, 50, IntPtr.Zero);
                    }
                    else if (msg.message == Native.WM_TIMER)
                    {
                        if (releasing)
                        {
                            if (held == 0 || Environment.TickCount - releaseStart >= ReleaseWaitMs) break;
                        }
                        else if (msg.wParam == reinstall) Install();
                    }
                }
                Native.KillTimer(IntPtr.Zero, reinstall);
                if (check != IntPtr.Zero) Native.KillTimer(IntPtr.Zero, check);
            }
            if (kbHook != IntPtr.Zero) Native.UnhookWindowsHookEx(kbHook);
            if (msHook != IntPtr.Zero) Native.UnhookWindowsHookEx(msHook);
            kbHook = msHook = IntPtr.Zero;
        }

        bool Install()
        {
            IntPtr module = Native.GetModuleHandle(null);
            IntPtr kb = Native.SetWindowsHookEx(13, kbProc, module, 0); // WH_KEYBOARD_LL
            IntPtr ms = Native.SetWindowsHookEx(14, msProc, module, 0); // WH_MOUSE_LL
            if (kb == IntPtr.Zero || ms == IntPtr.Zero)
            {
                if (kb != IntPtr.Zero) Native.UnhookWindowsHookEx(kb);
                if (ms != IntPtr.Zero) Native.UnhookWindowsHookEx(ms);
                return kbHook != IntPtr.Zero && msHook != IntPtr.Zero;
            }
            if (kbHook != IntPtr.Zero) Native.UnhookWindowsHookEx(kbHook);
            if (msHook != IntPtr.Zero) Native.UnhookWindowsHookEx(msHook);
            kbHook = kb;
            msHook = ms;
            return true;
        }

        uint CurrentMods()
        {
            uint mods = 0;
            if (keysDown.Contains(0xA2) || keysDown.Contains(0xA3)) mods |= Native.MOD_CONTROL;
            if (keysDown.Contains(0xA4) || keysDown.Contains(0xA5)) mods |= Native.MOD_ALT;
            if (keysDown.Contains(0xA0) || keysDown.Contains(0xA1)) mods |= Native.MOD_SHIFT;
            return mods;
        }

        void UpdateHeld()
        {
            int buttons = 0;
            for (int m = mouseMask; m != 0; m &= m - 1) buttons++;
            held = keysDown.Count + buttons;
        }

        IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0 || forceAll) return Native.CallNextHookEx(kbHook, nCode, wParam, lParam);
            try
            {
                int flags = Marshal.ReadInt32(lParam, 8);
                if ((flags & 0x10) != 0) return Native.CallNextHookEx(kbHook, nCode, wParam, lParam); // touche simulée
                uint vk = (uint)Marshal.ReadInt32(lParam, 0);
                int message = wParam.ToInt32();
                if (message == 0x100 || message == 0x104) // WM_KEYDOWN, WM_SYSKEYDOWN
                {
                    if (releasing) // rendu : seule la répétition d'une touche encore enfoncée reste retenue
                        return keysDown.Contains(vk) ? new IntPtr(1) : Native.CallNextHookEx(kbHook, nCode, wParam, lParam);
                    keysDown.Add(vk);
                    uint mods = CurrentMods();
                    // Sans exiger une touche « fraîche » ni l'absence d'autres touches : une touche relâchée pendant que
                    // Windows avait retiré le crochet resterait « enfoncée » ici, et le raccourci ne passerait plus.
                    if (vk == Native.VK_ESCAPE && (mods & AllMods) == AllMods) Rallumage.EmergencyExit("la sortie de secours (Ctrl+Alt+Maj+Échap)");
                    else if (vk == comboKey && (mods & comboMods) == comboMods) Rallumage.Request(true, true, "le raccourci");
                    if (vk == Native.VK_ESCAPE && !escHeld) { escStart = Environment.TickCount; escHeld = true; }
                    UpdateHeld();
                    return new IntPtr(1);
                }
                if (vk == Native.VK_ESCAPE) escHeld = false;
                if (keysDown.Remove(vk)) { UpdateHeld(); return new IntPtr(1); }
            }
            catch { }
            return Native.CallNextHookEx(kbHook, nCode, wParam, lParam); // une touche enfoncée avant l'extinction
        }

        IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0 || forceAll) return Native.CallNextHookEx(msHook, nCode, wParam, lParam);
            try
            {
                if ((Marshal.ReadInt32(lParam, 12) & 1) != 0) return Native.CallNextHookEx(msHook, nCode, wParam, lParam); // simulé
                int message = wParam.ToInt32();
                int button = -1;
                bool up = false;
                switch (message)
                {
                    case 0x201: button = 0; break;            // gauche
                    case 0x202: button = 0; up = true; break;
                    case 0x204: button = 1; break;            // droit
                    case 0x205: button = 1; up = true; break;
                    case 0x207: button = 2; break;            // milieu
                    case 0x208: button = 2; up = true; break;
                    case 0x20B: button = 3; break;            // boutons du côté
                    case 0x20C: button = 3; up = true; break;
                }
                int bit = button >= 0 ? 1 << button : 0;
                if (releasing) // rendu : seul le relâché d'un bouton encore enfoncé reste retenu
                {
                    if (up && (mouseMask & bit) != 0) { mouseMask &= ~bit; UpdateHeld(); return new IntPtr(1); }
                    return Native.CallNextHookEx(msHook, nCode, wParam, lParam);
                }
                if (button >= 0)
                {
                    if (up)
                    {
                        if ((mouseMask & bit) == 0) return Native.CallNextHookEx(msHook, nCode, wParam, lParam);
                        mouseMask &= ~bit;
                    }
                    else mouseMask |= bit;
                    UpdateHeld();
                }
                return new IntPtr(1); // une touche, un clic ou la souris réveillent seulement l'écran (le rappel, 5 s)
            }
            catch { return Native.CallNextHookEx(msHook, nCode, wParam, lParam); }
        }
    }

    // ---- La fenêtre noire, par-dessus tous les écrans, sur son propre fil ----

    // Son fil ne fait que ça : ni son, ni réseau, ni appel aux écrans. Elle part donc même si le reste du programme ne
    // répond plus, et elle se cache d'elle-même (toutes les 0,5 s) dès qu'un rallumage est demandé.
    class Overlay : Form
    {
        public string Hint = string.Empty;
        readonly Timer grab = new Timer { Interval = 500 };
        bool cursorHidden;

        public Overlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            grab.Tick += delegate
            {
                if (!Rallumage.Active || Rallumage.Requested) { Uncover(); return; }
                if (Native.IsWindowVisible(Handle)) Grab();
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW : pas dans Alt+Tab
                return cp;
            }
        }

        public void Cover()
        {
            if (!Rallumage.Active || Rallumage.Requested) return; // rallumé entre-temps
            Rectangle all = SystemInformation.VirtualScreen;
            Show();
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, all.X, all.Y, all.Width, all.Height, 0x40); // SWP_SHOWWINDOW
            if (!cursorHidden) { Cursor.Hide(); cursorHidden = true; }
            Grab();
            Invalidate();
            grab.Start();
        }

        public void Uncover()
        {
            grab.Stop();
            Hide();
            Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x97); // SWP_HIDEWINDOW, sans bouger ni activer : cachée pour de bon
            if (cursorHidden) { Cursor.Show(); cursorHidden = false; }
        }

        public void Grab()
        {
            if (Native.GetForegroundWindow() != Handle) Native.SetForegroundWindow(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case 0x0020: // WM_SETCURSOR : pas de flèche sur le noir
                    Native.SetCursor(IntPtr.Zero);
                    m.Result = new IntPtr(1);
                    return;
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
            base.OnFormClosing(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Rectangle all = SystemInformation.VirtualScreen;
            using (var font = new Font("Segoe UI", 14f))
            using (var brush = new SolidBrush(Color.FromArgb(110, 110, 110)))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                foreach (Screen screen in Screen.AllScreens)
                {
                    Rectangle r = screen.Bounds;
                    r.Offset(-all.X, -all.Y);
                    e.Graphics.DrawString(Hint, font, brush, r, format);
                }
        }
    }

    // Le fil de la fenêtre noire : il la crée au démarrage (cachée) et ne fait tourner que sa boucle de messages.
    class OverlayHost
    {
        Overlay form;

        public void Start()
        {
            var ready = new ManualResetEvent(false);
            var thread = new Thread(() =>
            {
                Application.ThreadException += (s, e) => Journal.Write("Erreur (fenêtre noire) : " + e.Exception);
                form = new Overlay();
                Rallumage.OverlayHwnd = form.Handle; // la fenêtre naît sur ce fil
                ready.Set();
                Application.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Name = "Fenêtre noire";
            thread.Start();
            if (!ready.WaitOne(5000)) Journal.Write("La fenêtre noire tarde à se créer.");
        }

        // Montre la fenêtre ; attend au plus timeoutMs que son fil l'ait fait.
        public bool Cover(string hint, int timeoutMs)
        {
            try
            {
                IAsyncResult r = form.BeginInvoke((MethodInvoker)delegate { form.Hint = hint; form.Cover(); });
                return r.AsyncWaitHandle.WaitOne(timeoutMs);
            }
            catch (Exception e) { Journal.Write("Fenêtre noire : " + e.Message); return false; }
        }

        // La cache : tout de suite par Windows, puis proprement par son fil.
        public void Uncover()
        {
            Native.ShowWindowAsync(Rallumage.OverlayHwnd, Native.SW_HIDE);
            try { if (form != null) form.BeginInvoke((MethodInvoker)delegate { form.Uncover(); }); }
            catch { }
        }
    }

    // ---- Le fil de garde ----

    // Il veille toutes les 0,25 s pendant l'extinction, sans dépendre d'aucun autre fil : Ctrl+Alt+Suppr (ou le verrou, une
    // fenêtre d'administrateur), Échap 10 s, les 12 heures. Puis, le rallumage demandé, il vérifie que
    // la fenêtre est bien partie et que le clavier et la souris sont rendus ; sinon, il force, et en dernier recours ferme
    // Eclipse et le relance. Il tient aussi la sortie de secours à part, au cas où Windows aurait retiré le blocage.
    class Watchdog
    {
        const int SecoursId = 2;
        static readonly int MaxBlockMs = (int)TimeSpan.FromHours(12).TotalMilliseconds;
        int desktopMisses, strayTicks;
        bool logged, hideRetried, woke, forced;

        public void Start()
        {
            var thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "Garde" };
            thread.Start();
        }

        void Run()
        {
            Native.MSG msg;
            Native.PeekMessage(out msg, IntPtr.Zero, 0x400, 0x400, 0); // crée la file de messages du fil
            if (!Native.RegisterHotKey(IntPtr.Zero, SecoursId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT | Native.MOD_NOREPEAT, Native.VK_ESCAPE))
                Journal.Write("Ctrl+Alt+Maj+Échap est déjà pris par une autre appli : seul le blocage du clavier le voit.");
            Native.SetTimer(IntPtr.Zero, IntPtr.Zero, 250, IntPtr.Zero);
            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                try
                {
                    if (msg.message == Native.WM_HOTKEY && msg.wParam.ToInt32() == SecoursId)
                        Rallumage.EmergencyExit("la sortie de secours (Ctrl+Alt+Maj+Échap, vue par Windows)");
                    else if (msg.message == Native.WM_TIMER) Tick();
                }
                catch (Exception e) { Journal.Write("Garde : " + e.Message); }
            }
        }

        void Tick()
        {
            int now = Environment.TickCount;
            InputBlocker b = Rallumage.Blocker;
            if (b != null && b.Releasing && b.Alive && now - b.ReleaseStart >= 8000)
            {
                b.ForceAll();
                Journal.Write("Clavier et souris rendus de force, 8 s après le rallumage.");
            }
            if (!Rallumage.Active)
            {
                desktopMisses = 0;
                logged = hideRetried = woke = false;
                // Une fenêtre noire visible hors extinction : jamais plus de 2 s.
                if (!Rallumage.OverlayVisible) strayTicks = 0;
                else if (++strayTicks >= 8)
                {
                    strayTicks = 0;
                    Native.ShowWindowAsync(Rallumage.OverlayHwnd, Native.SW_HIDE);
                    Journal.Write("Fenêtre noire restée hors extinction : cachée.");
                }
                return;
            }
            strayTicks = 0;
            if (Rallumage.Requested) { AfterRequest(now, b); return; }
            desktopMisses = Native.OnNormalDesktop() ? 0 : desktopMisses + 1;
            if (desktopMisses >= 3) Rallumage.Request(true, false, "un autre bureau de Windows (Ctrl+Alt+Suppr, verrou ou fenêtre d'administrateur)");
            else if (b != null && b.EscHeldLongEnough) Rallumage.Request(true, true, "Échap maintenu 10 s");
            else if (now - Rallumage.StartTick >= MaxBlockMs) Rallumage.Request(false, false, "12 heures écoulées");
        }

        void AfterRequest(int now, InputBlocker b)
        {
            if (!logged) { logged = true; Journal.Write("Rallumage demandé : " + Rallumage.Why + "."); }
            int elapsed = now - Rallumage.RequestTick;
            bool visible = Rallumage.OverlayVisible;
            // Les écrans : rallumés d'ici si le fil principal ne l'a pas fait 2 s après.
            if (Rallumage.Wake && !Rallumage.UiDone && !woke && elapsed >= 2000)
            {
                woke = true;
                Rallumage.WakeScreens(Rallumage.ByYou);
                Journal.Write("Le fil principal tarde : écrans rallumés par le fil de garde.");
            }
            if (visible && !hideRetried && elapsed >= 1000)
            {
                hideRetried = true;
                Native.ShowWindowAsync(Rallumage.OverlayHwnd, Native.SW_HIDE);
                if (b != null) b.ForceAll();
                Journal.Write("Fenêtre noire encore là 1 s après : cachée de nouveau, clavier et souris rendus.");
            }
            if (visible && Rallumage.Emergency && now - Rallumage.EmergencyTick >= 1500) HardExit("sortie de secours, et la fenêtre noire encore là 1,5 s après");
            else if (visible && elapsed >= 5000) HardExit("la fenêtre noire encore là 5 s après le rallumage");
            else if (!Rallumage.UiDone && elapsed >= 30000) HardExit("le fil principal ne répond plus depuis 30 s");
        }

        // Le dernier recours : Eclipse se ferme de force (Windows retire alors sa fenêtre et ses crochets, quoi qu'il
        // arrive) et se relance ; la nouvelle copie retire le muet.
        void HardExit(string why)
        {
            if (forced) return;
            forced = true;
            Journal.Write("Sortie forcée : " + why + ". Eclipse se ferme et se relance.");
            try { if (Rallumage.ScreenSaverWasOn) Native.SystemParametersInfoSet(17, 1, IntPtr.Zero, 0); } catch { } // SPI_SETSCREENSAVEACTIVE
            try { if (Rallumage.Wake) Rallumage.WakeScreens(Rallumage.ByYou); } catch { }
            try
            {
                var start = new ProcessStartInfo(Application.ExecutablePath, "/reprise " + Process.GetCurrentProcess().Id) { UseShellExecute = false };
                Process.Start(start);
            }
            catch (Exception e) { Journal.Write("Relance impossible : " + e.Message); }
            Process.GetCurrentProcess().Kill();
        }
    }

    // ---- La luminosité des écrans (DDC/CI, la norme par laquelle le PC parle aux réglages de l'écran) ----

    static class Ddc
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PHYSICAL_MONITOR { public IntPtr Handle; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEX
        {
            public int Size; public RECT Monitor; public RECT Work; public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICE
        {
            public int Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Text;
            public int State;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
        }

        public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);
        public const byte Brightness = 0x10; // le code VCP de la luminosité

        [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc fn, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);
        [DllImport("dxva2.dll")] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
        [DllImport("dxva2.dll")] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PHYSICAL_MONITOR[] list);
        [DllImport("dxva2.dll")] public static extern bool DestroyPhysicalMonitor(IntPtr handle);
        [DllImport("dxva2.dll", SetLastError = true)] public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr handle, byte code, IntPtr type, out uint current, out uint max);
        [DllImport("dxva2.dll", SetLastError = true)] public static extern bool SetVCPFeature(IntPtr handle, byte code, uint value);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }

    // Un écran, tel que le fil de la luminosité le voit. Les copies données aux autres fils ne contiennent pas le handle.
    class ScreenLight
    {
        public string Device = string.Empty; // \\.\DISPLAY1 : le lien avec Screen.AllScreens
        public string Name = string.Empty;   // le nom donné par l'écran (« VG27A », par exemple), lu dans Windows
        public Rectangle Bounds;
        public bool Ok;                      // l'écran répond en DDC/CI
        public int Percent;                  // 0 à 100
        public uint Max = 100;
        public IntPtr Physical;

        public ScreenLight Copy()
        {
            return new ScreenLight { Device = Device, Name = Name, Bounds = Bounds, Ok = Ok, Percent = Percent, Max = Max };
        }
    }

    // Tout passe par ce fil : un échange DDC/CI dure environ 60 ms par écran et peut traîner si un écran ne répond pas.
    // Pendant qu'on glisse un curseur, seule la dernière valeur voulue pour chaque écran est envoyée.
    class Brightness : IDisposable
    {
        readonly object gate = new object();
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        readonly Queue<Action> jobs = new Queue<Action>();
        readonly Dictionary<string, int> wanted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // lu par l'icône
        readonly HashSet<string> silentErrors = new HashSet<string>();
        List<ScreenLight> screens = new List<ScreenLight>(); // à ce fil seulement
        volatile bool stopping;

        public Brightness()
        {
            new Thread(Run) { IsBackground = true, Name = "Luminosité" }.Start();
        }

        // Le nom d'un écran (\\.\DISPLAY1 -> « VG27A »), s'il est déjà connu ; sinon null. Sans attendre.
        public string NameOf(string device)
        {
            string name;
            lock (names) return names.TryGetValue(device, out name) ? name : null;
        }

        // Relit les écrans et leur luminosité, puis done(copies), sur ce fil.
        public void Refresh(Action<List<ScreenLight>> done)
        {
            Post(delegate
            {
                Reload();
                if (done != null) done(Copies());
            });
        }

        public void Set(string device, int percent)
        {
            lock (gate) wanted[device] = Clamp(percent);
            signal.Set();
        }

        // Tous les écrans de delta points ; done(la nouvelle valeur du premier écran qui répond, ou -1 ; et de combien on
        // aurait voulu descendre sous 0, pour l'assombrissement), sur ce fil.
        // Une touche tenue enfoncée répète le raccourci plus vite que les écrans ne répondent : les pas s'additionnent
        // et partent ensemble, pour que tout s'arrête dès qu'on relâche.
        int stepDelta;
        bool stepQueued;
        Action<int, int> stepDone;

        public void Step(int delta, Action<int, int> done)
        {
            lock (gate)
            {
                stepDelta += delta;
                stepDone = done;
                if (stepQueued) return;
                stepQueued = true;
            }
            Post(delegate
            {
                int d;
                Action<int, int> report;
                lock (gate) { d = stepDelta; report = stepDone; stepDelta = 0; stepQueued = false; }
                if (screens.Count == 0) Reload();
                FlushWanted();
                int shown = -1, below = 0;
                foreach (ScreenLight s in screens)
                {
                    if (!s.Ok) continue;
                    if (shown < 0) below = Math.Max(0, -(s.Percent + d));
                    Apply(s, Clamp(s.Percent + d));
                    if (shown < 0) shown = s.Percent;
                }
                if (report != null) report(shown, below);
            });
        }

        void Post(Action job)
        {
            lock (gate) jobs.Enqueue(job);
            signal.Set();
        }

        static int Clamp(int v) { return v < 0 ? 0 : v > 100 ? 100 : v; }

        void Run()
        {
            while (!stopping)
            {
                Action job = null;
                lock (gate) if (jobs.Count > 0) job = jobs.Dequeue();
                try
                {
                    if (job != null) job();
                    else if (!FlushWanted()) signal.WaitOne();
                }
                catch (Exception e) { Journal.Write("Luminosité : " + e.Message); }
            }
        }

        // Envoie les valeurs voulues ; faux s'il n'y avait rien à envoyer.
        bool FlushWanted()
        {
            Dictionary<string, int> now;
            lock (gate)
            {
                if (wanted.Count == 0) return false;
                now = new Dictionary<string, int>(wanted, StringComparer.OrdinalIgnoreCase);
                wanted.Clear();
            }
            if (screens.Count == 0) Reload();
            foreach (KeyValuePair<string, int> kv in now)
                foreach (ScreenLight s in screens)
                    if (s.Ok && string.Equals(s.Device, kv.Key, StringComparison.OrdinalIgnoreCase)) Apply(s, kv.Value);
            return true;
        }

        void Apply(ScreenLight s, int percent)
        {
            uint raw = (uint)Math.Round(percent * s.Max / 100.0);
            if (Ddc.SetVCPFeature(s.Physical, Ddc.Brightness, raw)) { s.Percent = percent; return; }
            LogOnce("set " + s.Device, "Luminosité : l'écran " + s.Name + " a refusé le réglage (erreur " + Marshal.GetLastWin32Error() + ").");
        }

        void LogOnce(string key, string text)
        {
            if (silentErrors.Add(key)) Journal.Write(text);
        }

        List<ScreenLight> Copies()
        {
            var list = new List<ScreenLight>();
            foreach (ScreenLight s in screens) list.Add(s.Copy());
            return list;
        }

        void Release()
        {
            foreach (ScreenLight s in screens)
                if (s.Physical != IntPtr.Zero) Ddc.DestroyPhysicalMonitor(s.Physical);
            screens = new List<ScreenLight>();
        }

        // Les écrans branchés, de gauche à droite, avec leur nom et leur luminosité.
        void Reload()
        {
            Release();
            Dictionary<string, string> friendly = FriendlyNames();
            var found = new List<ScreenLight>();
            Ddc.MonitorEnumProc proc = delegate(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data)
            {
                var info = new Ddc.MONITORINFOEX { Size = Marshal.SizeOf(typeof(Ddc.MONITORINFOEX)) };
                if (!Ddc.GetMonitorInfo(monitor, ref info)) return true;
                var s = new ScreenLight
                {
                    Device = info.Device,
                    Bounds = Rectangle.FromLTRB(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom)
                };
                s.Name = NameFromWindows(info.Device, friendly);
                uint count;
                if (Ddc.GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out count) && count > 0)
                {
                    var physical = new Ddc.PHYSICAL_MONITOR[count];
                    if (Ddc.GetPhysicalMonitorsFromHMONITOR(monitor, count, physical))
                    {
                        s.Physical = physical[0].Handle;
                        for (int i = 1; i < physical.Length; i++) Ddc.DestroyPhysicalMonitor(physical[i].Handle);
                        uint current, max;
                        if (Ddc.GetVCPFeatureAndVCPFeatureReply(s.Physical, Ddc.Brightness, IntPtr.Zero, out current, out max) && max > 0)
                        {
                            s.Ok = true;
                            s.Max = max;
                            s.Percent = Clamp((int)Math.Round(current * 100.0 / max));
                        }
                        else LogOnce("get " + s.Device, "Luminosité : l'écran " + s.Name + " ne répond pas (DDC/CI désactivé dans son menu ?).");
                    }
                }
                found.Add(s);
                return true;
            };
            Ddc.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
            GC.KeepAlive(proc);
            found.Sort((a, b) => a.Bounds.X != b.Bounds.X ? a.Bounds.X.CompareTo(b.Bounds.X) : a.Bounds.Y.CompareTo(b.Bounds.Y));
            screens = found;
            lock (names)
            {
                names.Clear();
                foreach (ScreenLight s in found) names[s.Device] = s.Name;
            }
        }

        // \\.\DISPLAY1 -> l'identifiant de l'écran branché (DISPLAY\SAM0F6A\5&...&UID...) -> son nom dans Windows.
        static string NameFromWindows(string device, Dictionary<string, string> friendly)
        {
            var dd = new Ddc.DISPLAY_DEVICE { Size = Marshal.SizeOf(typeof(Ddc.DISPLAY_DEVICE)) };
            if (Ddc.EnumDisplayDevices(device, 0, ref dd, 1)) // EDD_GET_DEVICE_INTERFACE_NAME
            {
                string id = dd.Id ?? string.Empty;              // \\?\DISPLAY#SAM0F6A#5&...&UID4352#{e6f07b5f-...}
                if (id.StartsWith(@"\\?\")) id = id.Substring(4);
                int guid = id.IndexOf("#{", StringComparison.Ordinal);
                if (guid > 0) id = id.Substring(0, guid);
                id = id.Replace('#', '\\');
                string name;
                if (friendly.TryGetValue(id, out name)) return name;
                if (!string.IsNullOrEmpty(dd.Text) && dd.Text.IndexOf("Generic", StringComparison.OrdinalIgnoreCase) < 0) return dd.Text;
            }
            return "Écran " + Regex.Replace(device, @"\D", string.Empty);
        }

        // Les noms donnés par les écrans eux-mêmes (« VG27A », « U2720Q »...), par WMI.
        static Dictionary<string, string> FriendlyNames()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, UserFriendlyName FROM WmiMonitorID"))
                using (ManagementObjectCollection all = searcher.Get())
                    foreach (ManagementBaseObject o in all)
                    {
                        string instance = o["InstanceName"] as string;
                        ushort[] chars = o["UserFriendlyName"] as ushort[];
                        if (string.IsNullOrEmpty(instance) || chars == null) continue;
                        var sb = new StringBuilder();
                        foreach (ushort c in chars) if (c != 0) sb.Append((char)c);
                        if (sb.Length == 0) continue;
                        instance = Regex.Replace(instance, @"_\d+$", string.Empty); // DISPLAY\SAM0F6A\5&...&UID4352_0
                        map[instance] = sb.ToString().Trim();
                    }
            }
            catch (Exception e) { Journal.Write("Luminosité : noms des écrans illisibles (" + e.Message + ")."); }
            return map;
        }

        public void Dispose()
        {
            var done = new ManualResetEvent(false);
            Post(delegate { Release(); stopping = true; done.Set(); });
            done.WaitOne(2000);
        }
    }

    // Sous le minimum de l'écran : l'image elle-même est assombrie, par l'effet de couleur plein écran de Windows (celui
    // de la loupe et des filtres de couleur). Pas de fenêtre posée par-dessus : rien ne cache, rien ne bloque les clics,
    // et l'effet disparaît de lui-même si Eclipse s'arrête, même brutalement. Il vaut pour tous les écrans à la fois.
    // Essais du 15/11 : la table de gamma de Windows refuse d'assombrir de plus de 50 % ; l'effet de couleur, lui, va
    // aussi bas qu'on veut. On s'arrête à 90 % (Max), pour que l'image ne disparaisse jamais tout à fait.
    // Ce n'est pas plus de lumière économisée : le rétroéclairage reste au minimum, l'image devient plus sombre.
    // Toujours sur le fil principal (celui de l'icône), le seul qui appelle la loupe.
    class Dimmer : IDisposable
    {
        public const int Max = 90;
        [DllImport("magnification.dll")] static extern bool MagInitialize();
        [DllImport("magnification.dll")] static extern bool MagUninitialize();
        [DllImport("magnification.dll")] static extern bool MagSetFullscreenColorEffect(float[] matrix);
        bool ready, failed;
        int level;

        public int Level { get { return level; } } // 0 : rien ; 30 : l'image à 70 % de sa lumière

        public bool Set(int dim)
        {
            dim = Math.Max(0, Math.Min(Max, dim));
            if (dim == level) return true;
            if (!ready)
            {
                if (dim == 0) return true;
                if (failed) return false;
                if (!MagInitialize()) { failed = true; Journal.Write("Assombrissement impossible : la loupe de Windows refuse."); return false; }
                ready = true;
            }
            float k = 1f - dim / 100f;
            var m = new float[25];
            m[0] = m[6] = m[12] = k;  // rouge, vert, bleu
            m[18] = m[24] = 1f;       // transparence, et la ligne fixe
            if (!MagSetFullscreenColorEffect(m)) { Journal.Write("Assombrissement : l'effet de couleur a été refusé."); return false; }
            level = dim;
            if (dim == 0) { MagUninitialize(); ready = false; }
            return true;
        }

        public void Dispose() { Set(0); }
    }

    // Un curseur sombre, à la Windows 11 : glisser, molette (±5), flèches (±1), Page haut / bas (±10).
    // De Minimum (négatif : sous le minimum de l'écran, assombri par Eclipse) à 100. Le seuil, 0, est marqué d'un
    // trait et du mot « min » ; en glissant, le curseur s'y accroche (±2), pour le trouver sans viser.
    class Slider : Control
    {
        public static readonly Color Track = Color.FromArgb(90, 90, 90);
        public static readonly Color Fill = Color.FromArgb(76, 194, 255);
        public static readonly Color Below = Color.FromArgb(255, 170, 60); // la zone sous le minimum
        public static readonly Color BelowTrack = Color.FromArgb(95, 70, 40);
        int value;
        int minimum;
        bool dragging;
        public event EventHandler ValueChanged; // seulement quand c'est vous qui le bougez

        public int Minimum
        {
            get { return minimum; }
            set { minimum = Math.Min(0, value); Value = this.value; Invalidate(); }
        }

        // Le dessin du curseur, partagé avec le rappel en bas de l'écran.
        public static void PaintBar(Graphics g, RectangleF area, int minimum, int value, float thick, bool enabled, Font small, bool showMin)
        {
            float cy = area.Top + area.Height / 2f;
            Func<int, float> at = v => area.Left + area.Width * (v - minimum) / (float)(100 - minimum);
            float zero = at(0), x = at(value);
            using (var track = new Pen(Track, thick) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(track, zero, cy, area.Right, cy);
            if (minimum < 0)
                using (var below = new Pen(BelowTrack, thick) { StartCap = LineCap.Round, EndCap = LineCap.Flat })
                    g.DrawLine(below, area.Left, cy, zero, cy);
            Color fill = !enabled ? Track : value < 0 ? Below : Fill;
            using (var pen = new Pen(fill, thick) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (value > 0) g.DrawLine(pen, minimum < 0 ? zero : area.Left, cy, x, cy);
                else if (value < 0) g.DrawLine(pen, x, cy, zero, cy);
            }
            if (minimum < 0)
            {
                using (var tick = new Pen(Color.FromArgb(220, 220, 220), Math.Max(1.5f, thick / 2)))
                    g.DrawLine(tick, zero, cy - thick * 2.2f, zero, cy + thick * 2.2f);
                if (showMin && small != null)
                    using (var brush = new SolidBrush(Color.FromArgb(150, 150, 150)))
                    using (var f = new StringFormat { Alignment = StringAlignment.Center })
                        g.DrawString("min", small, brush, zero, cy + thick * 2.4f, f);
            }
        }

        public Slider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        public int Value
        {
            get { return value; }
            set { int v = Math.Max(minimum, Math.Min(100, value)); if (v != this.value) { this.value = v; Invalidate(); } }
        }

        void SetByUser(int v)
        {
            v = Math.Max(minimum, Math.Min(100, v));
            if (v == value) return;
            Value = v;
            if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }

        // Le bouton rond fait ~ la moitié de la hauteur de la barre ; le mot « min » prend le bas du contrôle.
        int Radius { get { return Math.Max(6, BarHeight / 3); } }
        int BarHeight { get { return minimum < 0 ? Height * 30 / 42 : Height; } }

        int FromX(int x, bool snap)
        {
            int r = Radius, w = Width - 2 * r;
            if (w <= 0) return 0;
            int v = (int)Math.Round(minimum + (x - r) * (100.0 - minimum) / w);
            if (snap && minimum < 0 && Math.Abs(v) <= 2) v = 0; // accroché au seuil
            return v;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : BackColor);
            int r = Radius, cy = BarHeight / 2, w = Width - 2 * r, thick = Math.Max(3, BarHeight / 8);
            using (var small = new Font("Segoe UI", 7.5f))
                PaintBar(g, new RectangleF(r, 0, w, BarHeight), minimum, value, thick, Enabled, small, true);
            float x = r + w * (value - minimum) / (float)(100 - minimum);
            Color fill = !Enabled ? Track : value < 0 ? Below : Fill;
            using (var outer = new SolidBrush(Color.FromArgb(69, 69, 69)))
                g.FillEllipse(outer, x - r, cy - r, 2 * r, 2 * r);
            float inner = r * (Focused || dragging ? 0.62f : 0.5f);
            using (var dot = new SolidBrush(fill))
                g.FillEllipse(dot, x - inner, cy - inner, 2 * inner, 2 * inner);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Enabled) return;
            Focus();
            dragging = true;
            SetByUser(FromX(e.X, true));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) SetByUser(FromX(e.X, true));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (Enabled) SetByUser(value + (e.Delta > 0 ? 5 : -5));
        }

        protected override bool IsInputKey(Keys key)
        {
            return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || base.IsInputKey(key);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int step = 0;
            if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) step = 1;
            else if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) step = -1;
            else if (e.KeyCode == Keys.PageUp) step = 10;
            else if (e.KeyCode == Keys.PageDown) step = -10;
            else if (e.KeyCode == Keys.Home) { SetByUser(minimum); return; }
            else if (e.KeyCode == Keys.End) { SetByUser(100); return; }
            if (step != 0) { SetByUser(value + step); e.Handled = true; }
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }

    // Le panneau qui s'ouvre d'un clic sur l'icône, près de l'heure : un curseur par écran, de gauche à droite.
    class BrightnessPanel : Form
    {
        const string LinkValue = "LierLuminosite";
        static readonly Color Back = Color.FromArgb(32, 32, 32);
        static readonly Color Dim = Color.FromArgb(170, 170, 170);
        readonly Brightness light;
        readonly Dimmer dimmer;
        readonly List<Slider> sliders = new List<Slider>();
        readonly List<Label> values = new List<Label>();
        readonly List<string> devices = new List<string>();
        readonly CheckBox link = new CheckBox();
        readonly Label status = new Label();
        string readStatus = "Lecture des écrans…";

        public BrightnessPanel(Brightness light, Dimmer dimmer, string offText, Action turnOff)
        {
            this.light = light;
            this.dimmer = dimmer;
            Text = "Eclipse : luminosité";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Back;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 10f);
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;

            var screensNow = new List<Screen>(Screen.AllScreens);
            screensNow.Sort((a, b) => a.Bounds.X != b.Bounds.X ? a.Bounds.X.CompareTo(b.Bounds.X) : a.Bounds.Y.CompareTo(b.Bounds.Y));
            int y = 14;
            var title = new Label { Text = "Luminosité", Font = new Font("Segoe UI Semibold", 11f), AutoSize = false };
            title.SetBounds(16, y, 300, 24);
            Controls.Add(title);
            y += 34;
            for (int i = 0; i < screensNow.Count; i++)
            {
                Screen sc = screensNow[i];
                string where = screensNow.Count == 1 ? string.Empty
                    : i == 0 ? "à gauche" : i == screensNow.Count - 1 ? "à droite" : "au milieu";
                if (sc.Primary) where = where.Length == 0 ? "principal" : where + ", principal";
                string name = light.NameOf(sc.DeviceName) ?? ("Écran " + (i + 1));
                var label = new Label { Text = name + (where.Length > 0 ? "  ·  " + where : string.Empty), AutoSize = false };
                label.SetBounds(16, y, 300, 22);
                Controls.Add(label);
                var slider = new Slider { Enabled = false, Minimum = -Dimmer.Max };
                slider.SetBounds(12, y + 24, 262, 42);
                var value = new Label { Text = "…", TextAlign = ContentAlignment.MiddleRight, ForeColor = Dim, AutoSize = false };
                value.SetBounds(270, y + 26, 56, 26);
                string device = sc.DeviceName;
                int index = i;
                slider.ValueChanged += delegate { OnSlide(index); };
                Controls.Add(slider);
                Controls.Add(value);
                sliders.Add(slider);
                values.Add(value);
                devices.Add(device);
                y += 74;
            }
            status.Text = readStatus;
            status.ForeColor = Dim;
            status.Font = new Font("Segoe UI", 9f);
            status.AutoSize = false;
            status.SetBounds(16, y - 6, 308, 40);
            Controls.Add(status);
            y += 38;
            link.Text = "Lier les écrans";
            link.Checked = LoadLink();
            link.SetBounds(16, y, 180, 26);
            link.CheckedChanged += delegate { SaveLink(link.Checked); };
            Controls.Add(link);
            var off = new Button { Text = offText, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(55, 55, 55), ForeColor = Color.White };
            off.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);
            off.SetBounds(16, y + 36, 308, 34);
            off.Click += delegate { Close(); turnOff(); };
            Controls.Add(off);
            ClientSize = new Size(340, y + 84);
            if (screensNow.Count < 2) link.Visible = false;

            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            Deactivate += delegate { Close(); };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                cp.ExStyle |= 0x00000080;    // WS_EX_TOOLWINDOW : jamais dans Alt+Tab
                return cp;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try { int round = 2; Ddc.DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { } // coins arrondis (Windows 11)
            Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
            int gap = (int)Math.Round(12 * DeviceScale());
            Location = new Point(work.Right - Width - gap, work.Bottom - Height - gap);
            light.Refresh(Fill);
        }

        float DeviceScale()
        {
            using (Graphics g = CreateGraphics()) return g.DpiX / 96f;
        }

        // Sur le fil de la luminosité : les valeurs lues, rendues au fil du panneau.
        void Fill(List<ScreenLight> read)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(delegate { ShowValues(read); })); } catch (InvalidOperationException) { }
        }

        void ShowValues(List<ScreenLight> read)
        {
            if (IsDisposed) return;
            int ok = 0;
            for (int i = 0; i < devices.Count; i++)
            {
                ScreenLight s = read.Find(x => string.Equals(x.Device, devices[i], StringComparison.OrdinalIgnoreCase));
                bool usable = s != null && s.Ok;
                sliders[i].Enabled = usable;
                if (usable) { Put(i, dimmer.Level > 0 ? -dimmer.Level : s.Percent); ok++; }
                else values[i].Text = "—";
            }
            readStatus = ok == devices.Count ? string.Empty
                : ok == 0 ? "Aucun écran ne répond. Activez DDC/CI dans le menu de vos écrans."
                : "Un écran ne répond pas : activez DDC/CI dans son menu.";
            UpdateStatus();
        }

        // Une valeur sur le curseur et dans le texte : « 40 % », ou en orange « −30 % » sous le minimum.
        void Put(int i, int v)
        {
            sliders[i].Value = v;
            values[i].Text = v < 0 ? "−" + (-v) + " %" : v + " %";
            values[i].ForeColor = v < 0 ? Slider.Below : Dim;
        }

        void UpdateStatus()
        {
            if (dimmer.Level > 0)
            {
                status.ForeColor = Slider.Below;
                status.Text = "Sous le minimum de l'écran : l'image est assombrie de " + dimmer.Level + " %, sur tous les écrans.";
            }
            else
            {
                status.ForeColor = Dim;
                status.Text = readStatus;
            }
        }

        // Au-dessus du seuil, la vraie luminosité de chaque écran. En dessous, les écrans restent à leur minimum et l'image
        // est assombrie, pour tous à la fois : les curseurs descendent donc ensemble, liés ou non.
        void OnSlide(int index)
        {
            int v = sliders[index].Value;
            if (v < 0)
            {
                for (int i = 0; i < sliders.Count; i++)
                {
                    if (!sliders[i].Enabled) continue;
                    Put(i, v);
                    light.Set(devices[i], 0);
                }
                dimmer.Set(-v);
                UpdateStatus();
                return;
            }
            bool wasDim = dimmer.Level > 0;
            if (wasDim) dimmer.Set(0);
            for (int i = 0; i < sliders.Count; i++)
            {
                if (!sliders[i].Enabled) continue;
                bool follow = i == index || link.Checked;
                if (!follow && !wasDim) continue;
                Put(i, follow ? v : 0); // non liés : remontés au seuil, là où ils étaient vraiment
                light.Set(devices[i], follow ? v : 0);
            }
            UpdateStatus();
        }

        static bool LoadLink()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Eclipse"))
                    return key == null || !(key.GetValue(LinkValue) is int) || (int)key.GetValue(LinkValue) != 0;
            }
            catch { return true; }
        }

        static void SaveLink(bool on)
        {
            try { using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Eclipse")) key.SetValue(LinkValue, on ? 1 : 0); }
            catch (Exception e) { Journal.Write("Luminosité : réglage « Lier » non enregistré (" + e.Message + ")."); }
        }
    }

    // Le rappel en bas de l'écran après Ctrl+Alt+Maj+Haut / Bas : « Luminosité 60 % », 1,5 s, sans prendre le clavier.
    class LightOsd : Form
    {
        readonly Timer hide = new Timer { Interval = 1500 };
        int percent;

        public LightOsd()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(32, 32, 32);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 10f);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            hide.Tick += delegate { hide.Stop(); Hide(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;                     // CS_DROPSHADOW
                cp.ExStyle |= 0x00000080 | 0x08000000 | 0x00000008; // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST
                return cp;
            }
        }

        public void Flash(int value)
        {
            percent = value;
            float scale;
            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
            Size = new Size((int)(300 * scale), (int)(64 * scale));
            Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(work.Left + (work.Width - Width) / 2, work.Bottom - Height - (int)(48 * scale));
            if (!Visible) { Show(); try { int round = 2; Ddc.DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { } }
            Invalidate();
            hide.Stop();
            hide.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float s = Height / 64f;
            string text = percent == NoScreen ? "Luminosité : aucun écran ne répond"
                : percent < 0 ? "Sous le minimum  −" + (-percent) + " %"
                : "Luminosité  " + percent + " %";
            using (var brush = new SolidBrush(percent < 0 && percent != NoScreen ? Slider.Below : ForeColor))
                g.DrawString(text, Font, brush, 16 * s, 8 * s);
            if (percent == NoScreen) return;
            using (var small = new Font("Segoe UI", 7.5f))
                Slider.PaintBar(g, new RectangleF(16 * s, 30 * s, Width - 32 * s, 14 * s), -Dimmer.Max, percent, 4 * s, true, small, true);
        }

        public const int NoScreen = int.MinValue;

        protected override void Dispose(bool disposing)
        {
            if (disposing) hide.Dispose();
            base.Dispose(disposing);
        }
    }

    // ---- Le compte à rebours de 5 secondes avant l'extinction ----

    class Countdown : Form
    {
        readonly Timer timer = new Timer();
        readonly Label label = new Label();
        readonly string shortcut;
        int remaining = 5;
        bool finished;
        public event EventHandler Elapsed;
        public event EventHandler Cancelled;

        public Countdown(string shortcut)
        {
            this.shortcut = shortcut;
            Text = "Eclipse";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(460, 190);
            Font = new Font("Segoe UI", 10f);
            label.SetBounds(16, 12, 428, 125);
            Controls.Add(label);
            var cancel = new Button { Text = "Annuler" };
            cancel.SetBounds(344, 145, 100, 32);
            cancel.Click += delegate { Cancel(); };
            Controls.Add(cancel);
            AcceptButton = cancel;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Cancel(); };
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Refresh2();
            timer.Interval = 1000;
            timer.Tick += delegate
            {
                remaining--;
                if (remaining > 0) { Refresh2(); return; }
                finished = true;
                timer.Stop();
                Close(); // d'abord : le compte à rebours ne doit pas rester au-dessus de l'écran noir
                if (Elapsed != null) Elapsed(this, EventArgs.Empty);
            };
            timer.Start();
        }

        void Refresh2()
        {
            label.Text = "Extinction de " + Screen.AllScreens.Length + " écran(s) dans " + remaining + " s.\n\nPour tout rallumer : "
                + shortcut + "\nSecours : Ctrl+Alt+Maj+Échap.";
        }

        public void Cancel() { Close(); }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            timer.Dispose();
            if (!finished && Cancelled != null) Cancelled(this, EventArgs.Empty);
        }
    }

    // ---- Le choix du raccourci ----

    class HotkeyDialog : Form
    {
        public uint Mods, Key;

        public HotkeyDialog()
        {
            Text = "Changer le raccourci";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            TopMost = true;
            KeyPreview = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(420, 100);
            Font = new Font("Segoe UI", 10f);
            var label = new Label { Text = "Faites votre combinaison : au moins deux touches parmi Ctrl, Alt et Maj, plus une autre touche." };
            label.SetBounds(16, 16, 388, 68);
            Controls.Add(label);
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            KeyDown += OnCapture;
        }

        void OnCapture(object sender, KeyEventArgs e)
        {
            e.SuppressKeyPress = e.Handled = true;
            Keys key = e.KeyCode;
            if (key == Keys.ControlKey || key == Keys.Menu || key == Keys.ShiftKey || key == Keys.LWin || key == Keys.RWin) return;
            if (key == Keys.Delete) return; // Ctrl+Alt+Suppr appartient à Windows
            uint mods = 0;
            int count = 0;
            if (e.Control) { mods |= Native.MOD_CONTROL; count++; }
            if (e.Alt) { mods |= Native.MOD_ALT; count++; }
            if (e.Shift) { mods |= Native.MOD_SHIFT; count++; }
            if (count < 2) return;
            Mods = mods;
            Key = (uint)key;
            DialogResult = DialogResult.OK;
        }
    }

    // ---- Le logo ----

    static class Logo
    {
        // L'éclipse. Près de l'heure, d'une seule couleur : le croissant de lumière, clair sur
        // une barre des tâches sombre, foncé sur une barre claire. L'icône du programme (Eclipse.ico) est la même
        // éclipse en couleur : un disque noir devant un disque blanc bleuté, avec un halo bleu, sur une tuile sombre.
        public static void Draw(Graphics g, int size, Color color)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.ScaleTransform(size / 64f, size / 64f);
            using (var light = Crescent(32, 32, 26, 36.5f, 27.5f, 24.2f))
            using (var brush = new SolidBrush(color))
                g.FillPath(brush, light);
        }

        // Le disque (x1, y1, r1) privé du disque (x2, y2, r2) : un croissant, aux bords lissés.
        public static GraphicsPath Crescent(float x1, float y1, float r1, float x2, float y2, float r2)
        {
            double dx = x2 - x1, dy = y2 - y1, d = Math.Sqrt(dx * dx + dy * dy);
            double a = (r1 * r1 - r2 * r2 + d * d) / (2 * d);
            double phi = Math.Atan2(dy, dx) * 180 / Math.PI;
            double alpha = Math.Acos(Math.Max(-1, Math.Min(1, a / r1))) * 180 / Math.PI;
            double beta = Math.Acos(Math.Max(-1, Math.Min(1, (d - a) / r2))) * 180 / Math.PI;
            var p = new GraphicsPath();
            p.AddArc(x1 - r1, y1 - r1, 2 * r1, 2 * r1, (float)(phi + alpha), (float)(360 - 2 * alpha));
            p.AddArc(x2 - r2, y2 - r2, 2 * r2, 2 * r2, (float)(phi + 180 + beta), (float)(-2 * beta));
            p.CloseFigure();
            return p;
        }
    }

    // ---- Le programme : l'icône près de l'heure, son menu, et les états ----

    class App : NativeWindow, IDisposable
    {
        enum State { Idle, Countdown, Blackout, LockedSession }

        const int HotkeyId = 1, LightUpId = 2, LightDownId = 3;
        const int LightStep = 10;
        const string RegKey = @"Software\Eclipse";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Secours = "Ctrl+Alt+Maj+Échap";
        static readonly TimeSpan RewakeGrace = TimeSpan.FromSeconds(5);
        static readonly TimeSpan SamePress = TimeSpan.FromSeconds(3);     // le même appui du raccourci, vu deux fois
        static readonly TimeSpan RelapseWatch = TimeSpan.FromSeconds(15); // écrans rééteints juste après le rallumage

        readonly NotifyIcon tray = new NotifyIcon();
        readonly ToolStripMenuItem offItem, screensItem;
        readonly OverlayHost overlay = new OverlayHost();
        readonly Worker sound = new Worker();
        readonly Watchdog watchdog = new Watchdog();
        readonly Timer guard = new Timer();
        readonly Brightness light = new Brightness();
        readonly Dimmer dimmer = new Dimmer();
        readonly Timer singleClick = new Timer(); // un clic ouvre la luminosité, un double-clic éteint : on attend de savoir
        BrightnessPanel panel;
        LightOsd osd;
        DateTime panelClosedAt;
        Countdown countdown;
        InputBlocker blocker;
        State state;
        readonly IntPtr powerNotify;
        IntPtr previousForeground;
        uint hotMods = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT;
        uint hotKey = (uint)Keys.E;
        int displayState = 1;         // 0 éteints, 1 allumés, 2 assombris (selon Windows)
        int offTries;                 // extinctions envoyées depuis que les écrans étaient éteints
        int savedScreenSaver = -1;
        DateTime lastOffSent, releasedAt, wokeAt;
        bool keepAwakeUntilWake;

        public App(bool afterRescue)
        {
            CreateHandle(new CreateParams { Caption = "EclipseHidden" });
            Rallumage.AppHwnd = Handle;
            overlay.Start();
            watchdog.Start();
            LoadHotkey();
            Guid displayStateGuid = new Guid("6FE69556-704A-47A0-8F24-C28D936FDA47"); // GUID_CONSOLE_DISPLAY_STATE
            powerNotify = Native.RegisterPowerSettingNotification(Handle, ref displayStateGuid, 0);

            var menu = new ContextMenuStrip();
            offItem = new ToolStripMenuItem(string.Empty, null, delegate { Begin(); });
            offItem.Font = new Font(offItem.Font, FontStyle.Bold);
            menu.Items.Add(offItem);
            menu.Items.Add("Luminosité des écrans...", null, delegate { OpenPanel(); });
            menu.Items.Add("Éteindre et verrouiller Windows", null, delegate { BeginLocked(); });
            menu.Items.Add(new ToolStripSeparator());
            screensItem = new ToolStripMenuItem(string.Empty) { Enabled = false };
            menu.Items.Add(screensItem);
            menu.Items.Add("Changer le raccourci...", null, delegate { ChangeHotkey(); });
            var startup = new ToolStripMenuItem("Lancer au démarrage de Windows") { CheckOnClick = true, Checked = StartupEnabled() };
            startup.Click += delegate { SetStartup(startup.Checked); };
            menu.Items.Add(startup);
            menu.Items.Add("Sorties de secours", null, delegate { ShowHelp(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quitter", null, delegate { Application.Exit(); });
            menu.Opening += delegate { RefreshLabels(); };

            tray.Icon = MakeIcon();
            tray.ContextMenuStrip = menu;
            singleClick.Interval = SystemInformation.DoubleClickTime;
            singleClick.Tick += delegate { singleClick.Stop(); OpenPanel(); };
            tray.MouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                singleClick.Stop();
                singleClick.Start();
            };
            tray.DoubleClick += delegate { singleClick.Stop(); Begin(); };
            tray.Visible = true;
            light.Refresh(null); // les noms des écrans, prêts pour le premier clic
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

            guard.Interval = 500;
            guard.Tick += delegate { GuardTick(); };
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.UserPreferenceChanged += OnThemeChanged;

            Journal.Write(afterRescue ? "Relancé après une sortie forcée." : "Démarré.");
            RestoreSoundWithRetries(); // un muet resté d'un arrêt brutal
            if (StartupEnabled()) SetStartup(true); // le chemin suit le programme s'il a été déplacé
            RefreshLabels();
            if (!Native.RegisterHotKey(Handle, LightUpId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT, Native.VK_UP)
                | !Native.RegisterHotKey(Handle, LightDownId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT, Native.VK_DOWN))
                Journal.Write("Luminosité : Ctrl+Alt+Maj+Haut ou Bas est déjà pris par une autre appli.");
            if (!RegisterCurrentHotkey())
                tray.ShowBalloonTip(5000, "Eclipse", "Le raccourci " + ShortcutText() + " est déjà pris par une autre appli. Changez-le depuis le menu.", ToolTipIcon.Warning);
            else if (afterRescue)
                tray.ShowBalloonTip(5000, "Eclipse", "Eclipse s'est relancé après une sortie de secours. Le son revient.", ToolTipIcon.Info);
        }

        static void Try(string what, Action step)
        {
            try { step(); }
            catch (Exception e) { Journal.Write("Échec (" + what + ") : " + e.Message); }
        }

        // ---- La luminosité ----

        void OpenPanel()
        {
            if (state != State.Idle) return;
            if (panel != null && !panel.IsDisposed) { panel.Activate(); return; }
            if (DateTime.UtcNow - panelClosedAt < TimeSpan.FromMilliseconds(400)) return; // ce clic vient de le fermer
            panel = new BrightnessPanel(light, dimmer, "Éteindre les écrans (" + ShortcutText() + ")", Begin);
            panel.FormClosed += delegate { panel = null; panelClosedAt = DateTime.UtcNow; };
            panel.Show();
            panel.Activate();
        }

        // Au clavier : la vraie luminosité d'abord ; arrivé au minimum de l'écran, on continue en assombrissant l'image,
        // et en remontant, l'assombrissement part avant que la vraie luminosité ne remonte.
        void StepLight(int delta)
        {
            if (dimmer.Level > 0)
            {
                int dim = dimmer.Level - delta;
                if (dim > 0) { dimmer.Set(dim); ShowOsd(-dimmer.Level); return; }
                dimmer.Set(0);
                if (dim == 0) { ShowOsd(0); return; }
                delta = -dim; // le reste remonte la vraie luminosité
            }
            IntPtr hwnd = Handle;
            light.Step(delta, (shown, below) => Native.PostMessage(hwnd, Native.WM_APP_LIGHT, new IntPtr(shown), new IntPtr(below)));
        }

        void ShowOsd(int value)
        {
            if (osd == null || osd.IsDisposed) osd = new LightOsd();
            osd.Flash(value);
        }

        void OnDisplayChanged(object sender, EventArgs e)
        {
            light.Refresh(null); // un écran branché, débranché ou déplacé : on relit tout
        }

        void Begin()
        {
            if (state != State.Idle) return;
            if (panel != null && !panel.IsDisposed) panel.Close();
            state = State.Countdown;
            previousForeground = Native.GetForegroundWindow();
            keepAwakeUntilWake = false;
            countdown = new Countdown(ShortcutText());
            countdown.Elapsed += delegate { countdown = null; Blackout(); };
            countdown.Cancelled += delegate { countdown = null; state = State.Idle; };
            countdown.Show();
            countdown.Activate();
        }

        void Blackout()
        {
            Rallumage.Begin();
            blocker = new InputBlocker(hotMods, hotKey);
            if (!blocker.Start())
            {
                blocker = null;
                Rallumage.Active = false;
                state = State.Idle;
                Journal.Write("Extinction impossible : le blocage du clavier et de la souris n'a pas pu se poser.");
                tray.ShowBalloonTip(5000, "Eclipse", "Impossible de bloquer le clavier et la souris : rien n'a été coupé.", ToolTipIcon.Error);
                return;
            }
            Rallumage.Blocker = blocker;
            state = State.Blackout;
            Journal.Write("Extinction (" + Screen.AllScreens.Length + " écran(s)).");
            // Un rallumage demandé pendant ces premières secondes (Ctrl+Alt+Suppr, une touche...) est fini ici même :
            // FinishRelease ne fait rien deux fois.
            if (Rallumage.Requested) { FinishRelease(); return; }
            // Avant d'éteindre : le muet, et les sorties de Sonar (celles des écrans disparaissent 2 s après), sur le fil du
            // son. S'il tarde, on éteint quand même.
            if (!sound.RunAndWait(delegate { Sound.Mute(); Sonar.Snapshot(); }, 6000))
                Journal.Write("Le son tarde à se couper : extinction quand même.");
            if (state != State.Blackout || Rallumage.Requested) { FinishRelease(); return; } // rallumé entre-temps
            Try("l'écran de veille", DisableScreenSaver);
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED);
            if (!overlay.Cover("Écrans éteints, PC allumé\nPour tout rallumer : " + ShortcutText() + "\nSecours : " + Secours, 3000))
                Journal.Write("La fenêtre noire tarde à s'afficher.");
            if (state != State.Blackout || Rallumage.Requested) { FinishRelease(); return; }
            displayState = 1;
            offTries = 0;
            ScreensOff();
            guard.Start();
        }

        void BeginLocked()
        {
            if (state != State.Idle) return;
            state = State.LockedSession;
            Journal.Write("Extinction avec verrouillage de Windows.");
            sound.RunAndWait(delegate { Sound.Mute(); Sonar.Snapshot(); }, 6000);
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED);
            if (!Native.LockWorkStation()) { FinishLocked(); return; }
            var once = new Timer { Interval = 1500 };
            once.Tick += delegate
            {
                once.Stop();
                once.Dispose();
                if (state == State.LockedSession) ScreensOff();
            };
            once.Start();
        }

        void FinishLocked()
        {
            RestoreSoundWithRetries();
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
            state = State.Idle;
        }

        void OnThemeChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General) tray.Icon = MakeIcon();
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock && state == State.LockedSession) FinishLocked();
        }

        // Le rallumage, côté fil principal. La fenêtre est déjà partie et le clavier et la souris reviennent
        // (Rallumage.Request) ; ici vient le reste, chaque étape à part : une qui échoue n'empêche pas les suivantes.
        void FinishRelease()
        {
            overlay.Uncover(); // déjà cachée ; redit au cas où
            if (state != State.Blackout) return;
            bool wake = Rallumage.Wake, byYou = Rallumage.ByYou;
            state = State.Idle;
            releasedAt = DateTime.UtcNow;
            guard.Stop();
            InputBlocker b = blocker;
            blocker = null;
            if (b != null) b.BeginRelease(); // le fil du blocage se retire seul
            Try("la fenêtre d'avant", delegate
            {
                if (previousForeground != IntPtr.Zero && Native.IsWindow(previousForeground)) Native.SetForegroundWindow(previousForeground);
            });
            Try("l'écran de veille", RestoreScreenSaver);
            keepAwakeUntilWake = !wake && displayState == 0;
            if (!keepAwakeUntilWake) Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
            if (wake)
            {
                wokeAt = DateTime.UtcNow;
                Try("les écrans", delegate { ScreensOn(byYou); });
            }
            Rallumage.UiDone = true;
            Rallumage.Active = false;
            Journal.Write("Rallumé" + (wake ? string.Empty : " (écrans laissés éteints)") + " : " + Rallumage.Why + ".");
            RestoreSoundWithRetries();
        }

        // Le son au retour, sur le fil du son : le muet retiré (réessayé 2 min), et les sorties de Sonar remises (surveillées
        // 90 s, car les écrans vont et viennent quelques secondes au rallumage). Si les écrans restent éteints (après 12 h),
        // Sonar attend le prochain rallumage. Un seul suivi à la fois ; un nouvel appel repousse ses échéances, et une
        // extinction l'arrête.
        readonly object soundGate = new object();
        bool soundQueued;
        DateTime soundUntil, sonarUntil;

        void RestoreSoundWithRetries()
        {
            lock (soundGate)
            {
                soundUntil = DateTime.UtcNow.AddMinutes(2);
                sonarUntil = DateTime.UtcNow.AddSeconds(90);
                if (soundQueued) return;
                soundQueued = true;
            }
            sound.Post(RestoreSoundLoop);
        }

        void RestoreSoundLoop() // sur le fil du son
        {
            lock (soundGate) soundQueued = false;
            if (Sound.Restore() && !Sonar.Pending) return;
            while (!sound.Pause(2000))
            {
                DateTime now = DateTime.UtcNow, soundEnd, sonarEnd;
                lock (soundGate) { soundEnd = soundUntil; sonarEnd = sonarUntil; }
                bool soundDone = Sound.Restore() || now >= soundEnd;
                bool watching = false;
                if (Sonar.Pending)
                {
                    bool inPlace = Sonar.Restore();
                    if (now < sonarEnd) watching = true;
                    else if (inPlace) Sonar.Forget();
                }
                if (soundDone && !watching) return;
            }
        }

        // Pendant l'extinction : un écran qui se rallume (la souris touchée) garde le rappel 5 s, puis on éteint à nouveau ;
        // une touche ou un clic font de même. Seuls le raccourci et les sorties de secours rallument. Le reste de la garde
        // est à son fil.
        void GuardTick()
        {
            if (state != State.Blackout || Rallumage.Requested) return;
            if (displayState != 0 && DateTime.UtcNow - lastOffSent >= RewakeGrace) ScreensOff();
        }

        // À la fenêtre d'Eclipse seulement ; à toutes les fenêtres de Windows seulement si la fois d'avant n'a pas suffi.
        // Envoyé partout, le message était traité en retard par un programme occupé, qui pouvait rééteindre les écrans
        // après le rallumage.
        void ScreensOff()
        {
            lastOffSent = DateTime.UtcNow;
            Native.SendMessage(Handle, Native.WM_SYSCOMMAND, new IntPtr(Native.SC_MONITORPOWER), new IntPtr(2));
            if (offTries++ > 0) Native.PostMessage(Native.HWND_BROADCAST, Native.WM_SYSCOMMAND, new IntPtr(Native.SC_MONITORPOWER), new IntPtr(2));
        }

        void ScreensOn(bool nudgeMouse)
        {
            Native.SendMessage(Handle, Native.WM_SYSCOMMAND, new IntPtr(Native.SC_MONITORPOWER), new IntPtr(-1));
            Native.SetThreadExecutionState(Native.ES_DISPLAY_REQUIRED);
            if (!nudgeMouse) return;
            Native.mouse_event(1, 1, 0, 0, IntPtr.Zero);  // MOUSEEVENTF_MOVE : un pixel à droite...
            Native.mouse_event(1, -1, 0, 0, IntPtr.Zero); // ... et retour
        }

        // L'écran de veille est suspendu pendant l'extinction (sans rien enregistrer dans Windows), puis remis.
        void DisableScreenSaver()
        {
            int active = 0;
            savedScreenSaver = Native.SystemParametersInfo(16, 0, ref active, 0) ? active : -1; // SPI_GETSCREENSAVEACTIVE
            if (savedScreenSaver == 1) Native.SystemParametersInfoSet(17, 0, IntPtr.Zero, 0);  // SPI_SETSCREENSAVEACTIVE
            Rallumage.ScreenSaverWasOn = savedScreenSaver == 1;
        }

        void RestoreScreenSaver()
        {
            if (savedScreenSaver == 1) Native.SystemParametersInfoSet(17, 1, IntPtr.Zero, 0);
            savedScreenSaver = -1;
            Rallumage.ScreenSaverWasOn = false;
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_HOTKEY:
                    if (m.WParam.ToInt32() == LightUpId || m.WParam.ToInt32() == LightDownId)
                    {
                        if (state == State.Idle) StepLight(m.WParam.ToInt32() == LightUpId ? LightStep : -LightStep);
                        return;
                    }
                    if (m.WParam.ToInt32() != HotkeyId) break;
                    if (state == State.Idle) { if (DateTime.UtcNow - releasedAt >= SamePress) Begin(); }
                    else if (state == State.Countdown && countdown != null) countdown.Cancel();
                    else if (state == State.Blackout) Rallumage.Request(true, true, "le raccourci (vu par Windows)");
                    return;
                case Native.WM_APP_RELEASE:
                    FinishRelease();
                    return;
                case Native.WM_APP_LIGHT:
                    if (state != State.Idle) return;
                    int shown = m.WParam.ToInt32(), below = m.LParam.ToInt32();
                    if (shown < 0) ShowOsd(LightOsd.NoScreen);
                    else if (below > 0 && dimmer.Set(below)) ShowOsd(-dimmer.Level); // au minimum : on assombrit l'image
                    else ShowOsd(shown);
                    return;
                case Native.WM_POWERBROADCAST:
                    if (m.WParam.ToInt32() == Native.PBT_POWERSETTINGCHANGE)
                    {
                        int display = Marshal.ReadInt32(m.LParam, 20); // POWERBROADCAST_SETTING.Data
                        // Un écran qui se rallume pendant l'extinction : le rappel reste 5 s, puis on éteint à nouveau.
                        if (state == State.Blackout && displayState == 0 && display != 0) lastOffSent = DateTime.UtcNow;
                        if (display == 0) offTries = 0;
                        displayState = display;
                        if (state == State.Idle && display == 1) RestoreSoundWithRetries();
                        // Rééteints dans les 15 s qui suivent le rallumage (un message d'extinction traité en retard) : rallumés.
                        if (state == State.Idle && display == 0 && DateTime.UtcNow - wokeAt < RelapseWatch)
                        {
                            Journal.Write("Écrans rééteints juste après le rallumage : rallumés de nouveau.");
                            Try("les écrans", delegate { ScreensOn(false); });
                        }
                        if (keepAwakeUntilWake && display != 0)
                        {
                            keepAwakeUntilWake = false;
                            if (state == State.Idle) Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
                        }
                        m.Result = new IntPtr(1);
                        return;
                    }
                    break;
                case Native.WM_ENDSESSION:
                    if (m.WParam != IntPtr.Zero) sound.RunAndWait(delegate { Sound.Restore(); }, 3000);
                    break;
            }
            base.WndProc(ref m);
        }

        string ShortcutText()
        {
            var sb = new StringBuilder();
            if ((hotMods & Native.MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((hotMods & Native.MOD_ALT) != 0) sb.Append("Alt+");
            if ((hotMods & Native.MOD_SHIFT) != 0) sb.Append("Maj+");
            sb.Append(((Keys)hotKey).ToString());
            return sb.ToString();
        }

        bool RegisterCurrentHotkey()
        {
            return Native.RegisterHotKey(Handle, HotkeyId, hotMods | Native.MOD_NOREPEAT, hotKey);
        }

        void LoadHotkey()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    if (key == null) return;
                    object mods = key.GetValue("Mods"), vk = key.GetValue("Key");
                    if (mods is int && vk is int) { hotMods = (uint)(int)mods; hotKey = (uint)(int)vk; }
                }
            }
            catch { }
        }

        void ChangeHotkey()
        {
            if (state != State.Idle) return;
            Native.UnregisterHotKey(Handle, HotkeyId);
            uint oldMods = hotMods, oldKey = hotKey;
            using (var dialog = new HotkeyDialog())
                if (dialog.ShowDialog() == DialogResult.OK) { hotMods = dialog.Mods; hotKey = dialog.Key; }
            if (!RegisterCurrentHotkey())
            {
                MessageBox.Show(ShortcutText() + " est déjà pris par une autre appli. On garde l'ancien.", "Eclipse");
                hotMods = oldMods;
                hotKey = oldKey;
                RegisterCurrentHotkey();
            }
            else if (hotMods != oldMods || hotKey != oldKey)
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKey))
                {
                    key.SetValue("Mods", (int)hotMods);
                    key.SetValue("Key", (int)hotKey);
                }
            }
            RefreshLabels();
        }

        void RefreshLabels()
        {
            int screens = Screen.AllScreens.Length;
            offItem.Text = "Éteindre les écrans (" + ShortcutText() + ")";
            screensItem.Text = screens + " écran(s) détecté(s)";
            string tip = "Eclipse : " + ShortcutText();
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        void ShowHelp()
        {
            MessageBox.Show("Le même raccourci éteint et rallume : " + ShortcutText()
                + "\n\nSortie de secours, tout de suite : " + Secours
                + "\n\nUne autre touche, un clic ou la souris réveillent seulement l'écran 5 secondes, le temps de lire le rappel."
                + "\n\nAutre sortie : Ctrl+Alt+Suppr, puis Annuler."
                + "\n\nAu bout de 12 heures, le clavier, la souris et le son reviennent seuls. Les écrans restent éteints jusqu'à ce que vous touchiez quelque chose."
                + "\n\nEn dernier recours, le bouton d'alimentation du PC marche toujours."
                + "\n\nLuminosité des écrans : un clic sur l'icône, ou Ctrl+Alt+Maj+Haut / Bas (10 % à la fois). Sous le seuil « min », l'image est assombrie au-delà du minimum de l'écran. Un double-clic sur l'icône éteint.",
                "Eclipse : sorties de secours");
        }

        static bool StartupEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && key.GetValue("Eclipse") != null;
            }
            catch { return false; }
        }

        static void SetStartup(bool on)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) key.SetValue("Eclipse", "\"" + Application.ExecutablePath + "\"");
                    else key.DeleteValue("Eclipse", false);
                }
            }
            catch { }
        }

        static bool TaskbarIsLight()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = key == null ? null : key.GetValue("SystemUsesLightTheme");
                    return v is int && (int)v == 1;
                }
            }
            catch { return false; }
        }

        static Icon MakeIcon()
        {
            int size = Math.Max(SystemInformation.SmallIconSize.Width, 16);
            Color color = TaskbarIsLight() ? Color.FromArgb(38, 38, 38) : Color.FromArgb(245, 245, 245);
            using (var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp)) Logo.Draw(g, size, color);
                IntPtr handle = bmp.GetHicon();
                using (Icon temp = Icon.FromHandle(handle))
                {
                    var icon = (Icon)temp.Clone();
                    Native.DestroyIcon(handle);
                    return icon;
                }
            }
        }

        public void Dispose()
        {
            if (state == State.Blackout)
            {
                Rallumage.Request(false, false, "Quitter");
                FinishRelease();
            }
            if (state == State.LockedSession) FinishLocked();
            InputBlocker b = blocker ?? Rallumage.Blocker;
            if (b != null) b.Stop();
            sound.RunAndWait(delegate { Sound.Restore(); }, 3000);
            Journal.Write("Quitté.");
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.UserPreferenceChanged -= OnThemeChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            Native.UnregisterHotKey(Handle, HotkeyId);
            Native.UnregisterHotKey(Handle, LightUpId);
            Native.UnregisterHotKey(Handle, LightDownId);
            singleClick.Dispose();
            if (panel != null && !panel.IsDisposed) panel.Dispose();
            if (osd != null && !osd.IsDisposed) osd.Dispose();
            dimmer.Dispose();
            light.Dispose();
            if (powerNotify != IntPtr.Zero) Native.UnregisterPowerSettingNotification(powerNotify);
            tray.Visible = false;
            tray.Dispose();
            DestroyHandle();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // « /reprise <numéro du processus> » : la relance après une sortie forcée ; on attend que l'ancienne copie parte.
            int previous = 0;
            if (args.Length >= 2 && args[0] == "/reprise") int.TryParse(args[1], out previous);
            if (previous != 0)
                try { using (Process old = Process.GetProcessById(previous)) old.WaitForExit(15000); } catch { }
            // Une erreur imprévue va au journal : jamais de fenêtre d'erreur, qui resterait cachée sous l'écran noir.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Journal.Write("Erreur : " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Journal.Write("Erreur grave : " + e.ExceptionObject);
            bool created;
            using (var mutex = new Mutex(true, "Eclipse_instance_unique", out created))
            {
                if (!created)
                {
                    bool owned = false;
                    try { owned = mutex.WaitOne(previous != 0 ? 5000 : 0); }
                    catch (AbandonedMutexException) { owned = true; } // l'ancienne copie s'est arrêtée brutalement
                    if (!owned)
                    {
                        if (previous != 0) Journal.Write("Relance abandonnée : l'ancienne copie d'Eclipse ne part pas.");
                        else MessageBox.Show("Eclipse tourne déjà : son icône est dans la barre des tâches (flèche ^ à côté de l'heure).", "Eclipse");
                        return;
                    }
                }
                Native.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var app = new App(previous != 0)) Application.Run();
            }
        }
    }
}
