// Eclipse — éteint les écrans sans mettre le PC en veille, bloque le clavier et la souris, coupe le son,
// règle la luminosité des écrans, jusque sous leur minimum, filtre la lumière bleue et passe un écran sur une console.
//
//   Ctrl+Alt+Maj+E            éteint, puis rallume (le raccourci se change depuis le menu de l'icône)
//   Ctrl+Alt+Maj+Échap        sortie de secours : tout revient, tout de suite
//   Clic sur l'icône          le panneau : la luminosité (un curseur par écran), la lumière bleue et les consoles
//   Double-clic sur l'icône   éteint
//   Ctrl+Alt+Maj+Haut / Bas   luminosité +10 % / -10 % sur tous les écrans
//   Ctrl+Alt+Maj+B            filtre la lumière bleue, ou plus (la chaleur se règle dans le panneau)
//   Ctrl+Alt+Maj+une lettre   passe un écran sur une console, puis le ramène sur le PC (menu de l'icône, Consoles)
//
// Un seul fichier, compilé par le compilateur du .NET Framework 4, déjà présent dans Windows : voir Compiler.bat.
// Eclipse n'écrit qu'à trois endroits : %LOCALAPPDATA%\Eclipse (journal, sorties rendues muettes), la clé
// HKCU\Software\Eclipse (raccourci, « Lier les écrans », lumière bleue, consoles) et, si vous le cochez, le
// lancement au démarrage (HKCU\...\Run). Pendant le jeu sur une console, il change aussi l'écran principal de
// Windows, remis au retour. Rien n'est envoyé sur Internet ; le seul échange réseau est local, avec SteelSeries
// Sonar s'il est installé.
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
//   Slider, LightOsd) ; sous le minimum, l'image elle-même est assombrie par l'effet de couleur de Windows, le même
//   qui filtre la lumière bleue (Dimmer) ;
// - les consoles : la même norme passe un écran sur une autre prise (Inputs, ConsoleInput, ConsoleDialog) ; pendant le
//   jeu sur l'écran principal, son voisin le devient (PrimaryScreen), puis tout revient ;
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
        public const int WM_APP_RUN = 0x8004;           // au fil principal : un travail rendu par le fil des écrans (consoles)
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
        [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint threadId);
        [DllImport("user32.dll")] public static extern uint MapVirtualKeyEx(uint code, uint type, IntPtr layout);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, [Out] char[] text, int size, uint flags, IntPtr layout);
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
        [DllImport("dxva2.dll", SetLastError = true)] public static extern bool GetCapabilitiesStringLength(IntPtr handle, out uint length);
        [DllImport("dxva2.dll", SetLastError = true, CharSet = CharSet.Ansi)] public static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr handle, StringBuilder caps, uint length);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // \\.\DISPLAY1 -> l'identifiant de l'écran branché (DISPLAY\ABC1234\5&...&UID1), stable d'un démarrage à
        // l'autre ; text : le nom que Windows lui donne. Vide si Windows ne le dit pas.
        public static string MonitorId(string device, out string text)
        {
            text = null;
            var dd = new DISPLAY_DEVICE { Size = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
            if (!EnumDisplayDevices(device, 0, ref dd, 1)) return string.Empty; // EDD_GET_DEVICE_INTERFACE_NAME
            text = dd.Text;
            string id = dd.Id ?? string.Empty;                                   // \\?\DISPLAY#ABC1234#5&...&UID1#{e6f07b5f-...}
            if (id.StartsWith(@"\\?\")) id = id.Substring(4);
            int guid = id.IndexOf("#{", StringComparison.Ordinal);
            if (guid > 0) id = id.Substring(0, guid);
            return id.Replace('#', '\\');
        }

        public static string MonitorId(string device)
        {
            string text;
            return MonitorId(device, out text);
        }

        // Les écrans tels que Windows les place à l'instant : Screen.AllScreens garde ceux d'avant un changement tant que
        // le fil principal n'a pas traité ses messages. Device : \\.\DISPLAY1 ; Flags & 1 : l'écran principal.
        public static List<MONITORINFOEX> Monitors()
        {
            var list = new List<MONITORINFOEX>();
            MonitorEnumProc proc = delegate(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data)
            {
                var info = new MONITORINFOEX { Size = Marshal.SizeOf(typeof(MONITORINFOEX)) };
                if (GetMonitorInfo(monitor, ref info)) list.Add(info);
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
            GC.KeepAlive(proc);
            return list;
        }

        // DISPLAY\ABC1234\5&...&UID1 -> ABC1234 : le fabricant et le modèle, sans le branchement.
        static string Model(string id)
        {
            string[] parts = (id ?? string.Empty).Split('\\');
            return parts.Length > 1 ? parts[1] : string.Empty;
        }

        // L'écran qui porte cet identifiant ; sinon le seul écran du même modèle (l'identifiant change si le pilote de la
        // carte graphique est réinstallé). -1 si aucun, ou si deux écrans du même modèle laissent un doute.
        public static int FindScreen(IList<string> ids, string id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            for (int i = 0; i < ids.Count; i++) if (string.Equals(ids[i], id, StringComparison.OrdinalIgnoreCase)) return i;
            string model = Model(id);
            int found = -1;
            if (model.Length == 0) return -1;
            for (int i = 0; i < ids.Count; i++)
            {
                if (!string.Equals(Model(ids[i]), model, StringComparison.OrdinalIgnoreCase)) continue;
                if (found >= 0) return -1;
                found = i;
            }
            return found;
        }
    }

    // Un écran, tel que le fil de la luminosité le voit. Les copies données aux autres fils ne contiennent pas le handle.
    class ScreenLight
    {
        public string Device = string.Empty; // \\.\DISPLAY1 : le lien avec Screen.AllScreens
        public string Id = string.Empty;     // DISPLAY\ABC1234\5&...&UID1 : le même d'un démarrage à l'autre
        public string Name = string.Empty;   // le nom donné par l'écran (souvent son modèle), lu dans Windows
        public Rectangle Bounds;
        public bool Ok;                      // l'écran répond en DDC/CI
        public int Percent;                  // 0 à 100
        public uint Max = 100;
        public int[] Inputs;                 // ses entrées (code 0x60), lues seulement pour le questionnaire des consoles
        public int Input = -1;               // l'entrée affichée, lue avec Inputs ; -1 : inconnue
        public IntPtr Physical;

        public ScreenLight Copy()
        {
            return new ScreenLight { Device = Device, Id = Id, Name = Name, Bounds = Bounds, Ok = Ok, Percent = Percent, Max = Max,
                Inputs = Inputs, Input = Input };
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

        // Le nom d'un écran (\\.\DISPLAY1 -> son modèle, en général), s'il est déjà connu ; sinon null. Sans attendre.
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
                string text;
                s.Id = Ddc.MonitorId(info.Device, out text);
                s.Name = NameFromWindows(info.Device, s.Id, text, friendly);
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

        // L'identifiant de l'écran branché (DISPLAY\ABC1234\5&...&UID...) -> son nom dans Windows.
        static string NameFromWindows(string device, string id, string text, Dictionary<string, string> friendly)
        {
            string name;
            if (id.Length > 0 && friendly.TryGetValue(id, out name)) return name;
            if (!string.IsNullOrEmpty(text) && text.IndexOf("Generic", StringComparison.OrdinalIgnoreCase) < 0) return text;
            return "Écran " + Regex.Replace(device, @"\D", string.Empty);
        }

        // ---- Les entrées des écrans, pour les consoles ----

        // Les entrées de chaque écran (sa chaîne de capacités : 1 à 2 s par écran) et celle qu'il affiche, puis
        // done(copies), sur ce fil.
        public void ReadInputs(Action<List<ScreenLight>> done)
        {
            Post(delegate
            {
                Reload();
                foreach (ScreenLight s in screens)
                {
                    if (s.Physical == IntPtr.Zero || !s.Ok) continue;
                    s.Inputs = Inputs.FromCapabilities(Capabilities(s.Physical));
                    if (s.Inputs != null) s.Input = ReadInput(s.Physical);
                }
                done(Copies());
            });
        }

        // L'entrée qu'affiche un écran, puis done(son code, ou -1 s'il ne répond pas), sur ce fil.
        public void GetInput(string id, Action<int> done)
        {
            Post(delegate
            {
                ScreenLight s = Find(id);
                done(s == null ? -1 : ReadInput(s.Physical));
            });
        }

        // Passe un écran sur une entrée, puis done(vrai si l'écran l'a acceptée), sur ce fil. Juste après un changement
        // d'entrée ou d'écran principal, l'écran ne répond plus pendant 1 à 2 s : on réessaie pendant 5 s.
        public void SetInput(string id, int code, Action<bool> done)
        {
            Post(delegate
            {
                var until = DateTime.UtcNow.AddSeconds(5);
                ScreenLight s = Find(id);
                int error = 0;
                for (int attempt = 1; ; attempt++)
                {
                    if (s != null && Ddc.SetVCPFeature(s.Physical, Inputs.Code, (uint)code)) { if (done != null) done(true); return; }
                    error = Marshal.GetLastWin32Error();
                    if (DateTime.UtcNow > until) break;
                    Thread.Sleep(300);
                    if (s == null || attempt % 5 == 0) { Reload(); s = Find(id); } // un handle neuf, si Windows a refait l'écran
                }
                Journal.Write("Consoles : l'écran " + (s != null ? s.Name : "de la console") + " n'a pas pris l'entrée "
                    + Inputs.NameOf(code) + (s == null ? " (débranché ?)." : " (erreur " + error + ")."));
                if (done != null) done(false);
            });
        }

        ScreenLight Find(string id)
        {
            if (screens.Count == 0) Reload();
            int i = Ddc.FindScreen(screens.ConvertAll(s => s.Id), id);
            return i < 0 || screens[i].Physical == IntPtr.Zero ? null : screens[i];
        }

        // Juste après un changement d'entrée, la réponse peut manquer : jusqu'à 6 essais.
        static int ReadInput(IntPtr physical)
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                uint current, max;
                if (Ddc.GetVCPFeatureAndVCPFeatureReply(physical, Inputs.Code, IntPtr.Zero, out current, out max)) return (int)(current & 0xFF);
                Thread.Sleep(300);
            }
            return -1;
        }

        static string Capabilities(IntPtr physical)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                uint length;
                if (Ddc.GetCapabilitiesStringLength(physical, out length) && length > 0)
                {
                    var caps = new StringBuilder((int)length);
                    if (Ddc.CapabilitiesRequestAndCapabilitiesReply(physical, caps, length)) return caps.ToString();
                }
                Thread.Sleep(200);
            }
            return null;
        }

        // Les noms donnés par les écrans eux-mêmes (leur modèle, en général), par WMI.
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
                        instance = Regex.Replace(instance, @"_\d+$", string.Empty); // DISPLAY\ABC1234\5&...&UID1_0
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

    // ---- Les consoles : passer un écran sur une console au clavier (DDC/CI, code 0x60), et revenir sur le PC ----

    // Les entrées d'un écran, numérotées par la norme MCCS, et le raccourci proposé pour une console.
    static class Inputs
    {
        public const byte Code = 0x60; // le code VCP de l'entrée

        static readonly string[] Names = { null, "VGA 1", "VGA 2", "DVI 1", "DVI 2", "Composite 1", "Composite 2",
            "S-Vidéo 1", "S-Vidéo 2", "Tuner 1", "Tuner 2", "Tuner 3", "Composante 1", "Composante 2", "Composante 3",
            "DisplayPort 1", "DisplayPort 2", "HDMI 1", "HDMI 2" };

        public static string NameOf(int code)
        {
            return code > 0 && code < Names.Length ? Names[code] : "Entrée " + code.ToString("X2");
        }

        // La liste du code 60 dans la partie vcp(...) de la chaîne de capacités : « vcp(10 14(05 08) 60(11 12 0F) 62) ».
        // null si l'écran n'annonce pas le code 60 ; vide s'il l'annonce sans dire ses entrées.
        public static int[] FromCapabilities(string caps)
        {
            int i = VcpStart(caps);
            if (i < 0) return null;
            for (int depth = 0; i < caps.Length; i++)
            {
                char c = caps[i];
                if (c == '(') depth++;
                else if (c == ')') { if (depth == 0) break; depth--; }
                else if (depth == 0 && IsHex(c))
                {
                    int end = HexEnd(caps, i);
                    bool sixty = end - i == 2 && caps[i] == '6' && caps[i + 1] == '0';
                    i = end - 1;
                    if (sixty) return List(caps, end);
                }
            }
            return null;
        }

        // Juste après « vcp( » ; pas « vcpname( », qui donne des noms et non des codes.
        static int VcpStart(string caps)
        {
            if (string.IsNullOrEmpty(caps)) return -1;
            for (int i = caps.IndexOf("vcp", StringComparison.OrdinalIgnoreCase); i >= 0;
                 i = caps.IndexOf("vcp", i + 3, StringComparison.OrdinalIgnoreCase))
            {
                if (i > 0 && char.IsLetter(caps[i - 1])) continue;
                int j = i + 3;
                while (j < caps.Length && caps[j] == ' ') j++;
                if (j < caps.Length && caps[j] == '(') return j + 1;
            }
            return -1;
        }

        // Les codes entre parenthèses après « 60 », deux chiffres hexadécimaux chacun, collés ou non.
        static int[] List(string caps, int i)
        {
            var codes = new List<int>();
            while (i < caps.Length && caps[i] == ' ') i++;
            if (i >= caps.Length || caps[i] != '(') return codes.ToArray();
            for (i++; i < caps.Length && caps[i] != ')'; i++)
            {
                if (!IsHex(caps[i])) continue;
                int end = HexEnd(caps, i);
                for (int k = i; k + 1 < end; k += 2)
                {
                    int code = Convert.ToInt32(caps.Substring(k, 2), 16);
                    if (!codes.Contains(code)) codes.Add(code);
                }
                i = end - 1;
            }
            return codes.ToArray();
        }

        static bool IsHex(char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'); }

        static int HexEnd(string s, int i)
        {
            while (i < s.Length && IsHex(s[i])) i++;
            return i;
        }

        // Les prises à proposer dans le questionnaire : celles que l'écran annonce ; s'il annonce le code 60 sans sa liste,
        // les prises courantes de la norme (« Voir » montre lesquelles existent). null : il ne change pas d'entrée.
        public static int[] Offered(int[] announced)
        {
            if (announced == null || announced.Length > 0) return announced;
            return new[] { 0x11, 0x12, 0x0F, 0x10 };
        }
    }

    // Les raccourcis clavier : leur texte, ceux des consoles, et les combinaisons qui ne peuvent pas servir.
    // Les modificateurs sont ceux de RegisterHotKey : MOD_ALT 1, MOD_CONTROL 2, MOD_SHIFT 4.
    static class Hotkeys
    {
        public const uint Cas = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT; // Ctrl+Alt+Maj

        // « Ctrl+Alt+Maj+X » : les lettres et les chiffres tels quels (pas « D1 »), les autres touches par leur nom.
        public static string Text(uint mods, uint key)
        {
            var sb = new StringBuilder();
            if ((mods & Native.MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((mods & Native.MOD_ALT) != 0) sb.Append("Alt+");
            if ((mods & Native.MOD_SHIFT) != 0) sb.Append("Maj+");
            return sb.Append(KeyName(key)).ToString();
        }

        static string KeyName(uint key)
        {
            if ((key >= 'A' && key <= 'Z') || (key >= '0' && key <= '9')) return ((char)key).ToString();
            if (key >= (uint)Keys.NumPad0 && key <= (uint)Keys.NumPad9) return "Pavé " + (key - (uint)Keys.NumPad0);
            switch ((Keys)key)
            {
                case Keys.Up: return "Haut";
                case Keys.Down: return "Bas";
                case Keys.Left: return "Gauche";
                case Keys.Right: return "Droite";
                case Keys.Escape: return "Échap";
                case Keys.Space: return "Espace";
                case Keys.Enter: return "Entrée";
                case Keys.PageUp: return "Pg préc";
                case Keys.PageDown: return "Pg suiv";
                case Keys.Home: return "Début";
                case Keys.End: return "Fin";
                case Keys.Insert: return "Inser";
                case Keys.Back: return "Ret. arrière";
                case Keys.CapsLock: return "Verr. maj";
                case Keys.Apps: return "Menu";
            }
            // Une touche de ponctuation : ce qu'elle écrit sur ce clavier (« ù », « $ ») ; sinon son nom (F5, Home…).
            // 2 : MAPVK_VK_TO_CHAR ; le bit du haut marque une touche morte.
            uint ch = Native.MapVirtualKeyEx(key, 2, Native.GetKeyboardLayout(0)) & 0x7FFF;
            return ch > ' ' ? char.ToUpperInvariant((char)ch).ToString() : ((Keys)key).ToString();
        }

        // Les modificateurs relus dans le registre : au moins deux parmi Ctrl, Alt et Maj, sinon Ctrl+Alt+Maj. Jamais une
        // touche seule, ou avec un seul modificateur : elle serait prise au clavier dans toutes les applis.
        public static uint ModsFrom(object value)
        {
            uint mods = value is int ? (uint)(int)value & Cas : 0;
            int count = 0;
            for (uint bit = 1; bit <= 4; bit <<= 1) if ((mods & bit) != 0) count++;
            return count >= 2 ? mods : Cas;
        }

        // Le raccourci d'une console, avec Ctrl+Alt+Maj : la première lettre (ou le premier chiffre) de son nom, sinon le
        // premier chiffre libre ; 0 si tout est pris. B est à la lumière bleue ; taken : les touches déjà prises.
        public static uint KeyFor(string name, ICollection<uint> taken)
        {
            string n = (name ?? string.Empty).Trim().ToUpperInvariant();
            if (n.Length > 0)
            {
                char c = n[0];
                if (((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) && c != 'B' && !taken.Contains(c)) return c;
            }
            for (char d = '1'; d <= '9'; d++) if (!taken.Contains(d)) return d;
            return 0;
        }

        // Les raccourcis des lignes du questionnaire, dans l'ordre. Une ligne au raccourci choisi le garde ; les autres
        // reçoivent Ctrl+Alt+Maj et la touche de KeyFor, qui évite taken et les raccourcis choisis. Une ligne sans nom
        // n'a pas de raccourci, ni de choix : son nom retapé reçoit la lettre automatique.
        public static void Assign(List<ConsoleInput> list, ICollection<uint> taken)
        {
            var used = new List<uint>(taken);
            foreach (ConsoleInput c in list)
                if (c.Chosen && c.Mods == Cas && c.Name.Trim().Length > 0) used.Add(c.Key);
            foreach (ConsoleInput c in list)
            {
                if (c.Name.Trim().Length == 0) { c.Chosen = false; c.Mods = Cas; c.Key = 0; continue; }
                if (c.Chosen) continue;
                c.Mods = Cas;
                c.Key = KeyFor(c.Name, used);
                if (c.Key != 0) used.Add(c.Key);
            }
        }

        // Ce qu'une combinaison fait déjà dans Eclipse, ou null. Le raccourci principal, qui se change, n'en est pas.
        public static string InEclipse(uint mods, uint key)
        {
            if (mods != Cas) return null;
            if (key == (uint)Keys.B) return "la lumière bleue";
            if (key == Native.VK_UP || key == Native.VK_DOWN) return "la luminosité";
            if (key == Native.VK_ESCAPE) return "la sortie de secours";
            return null;
        }

        // Ce qu'AltGr (Ctrl+Alt) écrit avec cette touche sur ce clavier, ou null. Un raccourci Ctrl+Alt+<touche> empêcherait
        // de l'écrire : « € » avec E, « @ » avec 0 sur un clavier français.
        public static string AltGrChar(uint mods, uint key, IntPtr layout)
        {
            const uint altGr = Native.MOD_CONTROL | Native.MOD_ALT;
            if ((mods & altGr) != altGr) return null;
            var state = new byte[256];
            state[0x11] = state[0x12] = 0x80; // VK_CONTROL, VK_MENU
            if ((mods & Native.MOD_SHIFT) != 0) state[0x10] = 0x80; // VK_SHIFT
            var text = new char[8];
            // 4 : sans toucher à l'état du clavier (une touche morte en attente) ; < 0 : une touche morte, qui écrit aussi.
            int n = Native.ToUnicodeEx(key, Native.MapVirtualKeyEx(key, 0, layout), state, text, text.Length, 4, layout);
            if (n < 0) n = 1;
            return n > 0 && text[0] >= ' ' ? new string(text, 0, Math.Min(n, text.Length)) : null;
        }

        // Pourquoi une combinaison ne peut pas servir de raccourci, ou null : elle sert déjà dans Eclipse, ou AltGr
        // l'utilise pour écrire.
        public static string Refusal(uint mods, uint key, IntPtr layout)
        {
            string use = InEclipse(mods, key);
            if (use != null) return Text(mods, key) + " sert déjà à " + use + ".";
            string ch = AltGrChar(mods, key, layout);
            if (ch == null) return null;
            return Text(mods, key) + " empêcherait d'écrire « " + ch + " » (AltGr+" + ((mods & Native.MOD_SHIFT) != 0 ? "Maj+" : string.Empty)
                + KeyName(key) + ").";
        }

        // Pourquoi une combinaison ne peut pas aller à une console, ou null : c'est le raccourci principal (mainMods,
        // mainKey), ou Refusal.
        public static string ForConsole(uint mods, uint key, uint mainMods, uint mainKey, IntPtr layout)
        {
            if (mods == mainMods && key == mainKey) return Text(mods, key) + " sert déjà à éteindre les écrans.";
            return Refusal(mods, key, layout);
        }

        // Pourquoi une combinaison ne peut pas devenir le raccourci principal, ou null : une console l'a, ou Refusal.
        public static string ForMain(uint mods, uint key, List<ConsoleInput> consoles, IntPtr layout)
        {
            ConsoleInput c = consoles.Find(x => x.Key == key && x.Mods == mods);
            if (c != null) return Text(mods, key) + " sert déjà à " + c.Name + ".";
            return Refusal(mods, key, layout);
        }
    }

    // Ce que montre l'écran du jeu, d'après l'entrée lue : une console réglée, le PC, ou rien de sûr.
    enum Seen { Nothing, Console, Pc }

    // Une console : une entrée d'un écran, nommée dans le questionnaire, et son raccourci (Mods + Key).
    // Rangées dans HKCU\Software\Eclipse\Consoles\1, 2... : Nom, Ecran, Entree, EntreePC, Touche, Modificateurs, Choisi.
    class ConsoleInput
    {
        const string RegKey = @"Software\Eclipse\Consoles";
        public string Name = string.Empty; // « Xbox »
        public string Screen = string.Empty; // l'identifiant de l'écran (Ddc.MonitorId)
        public int Input;                  // l'entrée de la console (0x11 : HDMI 1)
        public int Home;                   // l'entrée du PC sur cet écran (0x0F : DisplayPort 1)
        public uint Mods = Hotkeys.Cas;    // Ctrl+Alt+Maj, sauf un raccourci choisi
        public uint Key;                   // 0 : pas de raccourci
        public bool Chosen;                // raccourci choisi dans le questionnaire : il ne suit plus le nom
        public bool Refused;               // le raccourci est déjà pris par une autre appli (pas rangé dans le registre)

        public string Shortcut { get { return Key == 0 ? string.Empty : Hotkeys.Text(Mods, Key); } }

        // Le raccourci à annoncer (menu, message, panneau), ou null : pas de touche, ou touche prise ailleurs.
        public string WorkingShortcut { get { return Key != 0 && !Refused ? Shortcut : null; } }

        // L'entrée lue sur l'écran du jeu (code < 0 : pas de réponse). Une seule règle, pour la lecture des 3 s comme au
        // démarrage : le jeu ne se termine que si l'écran montre l'entrée du PC. Pas de réponse (erreur passagère juste
        // après une bascule, écran pas encore prêt) ou une prise sans console : on attend. Plus aucune console réglée
        // sur cet écran : le jeu n'a plus de sens, c'est le PC.
        public static Seen Read(int code, string screen, List<ConsoleInput> consoles, out ConsoleInput shown)
        {
            List<ConsoleInput> mine = consoles.FindAll(c => string.Equals(c.Screen, screen, StringComparison.OrdinalIgnoreCase));
            shown = code < 0 ? null : mine.Find(c => c.Input == code);
            if (shown != null) return Seen.Console;
            if (mine.Count == 0 || mine.Exists(c => c.Home == code)) return Seen.Pc;
            return Seen.Nothing;
        }

        public static List<ConsoleInput> Load()
        {
            var list = new List<ConsoleInput>();
            try
            {
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    if (root == null) return list;
                    string[] names = root.GetSubKeyNames();
                    Array.Sort(names, (a, b) => Number(a).CompareTo(Number(b)));
                    foreach (string sub in names)
                        using (RegistryKey k = root.OpenSubKey(sub))
                        {
                            var c = new ConsoleInput
                            {
                                Name = k.GetValue("Nom") as string ?? string.Empty,
                                Screen = k.GetValue("Ecran") as string ?? string.Empty,
                                Input = k.GetValue("Entree") is int ? (int)k.GetValue("Entree") : 0,
                                Home = k.GetValue("EntreePC") is int ? (int)k.GetValue("EntreePC") : 0,
                                Key = k.GetValue("Touche") is int ? (uint)(int)k.GetValue("Touche") : 0,
                                Mods = Hotkeys.ModsFrom(k.GetValue("Modificateurs")),
                                Chosen = k.GetValue("Choisi") is int && (int)k.GetValue("Choisi") != 0
                            };
                            if (c.Name.Trim().Length > 0 && c.Screen.Length > 0 && c.Input > 0 && c.Home > 0 && c.Input != c.Home) list.Add(c);
                        }
                }
            }
            catch (Exception e) { Journal.Write("Consoles : réglages illisibles (" + e.Message + ")."); }
            return list;
        }

        static int Number(string s)
        {
            int n;
            return int.TryParse(s, out n) ? n : int.MaxValue;
        }

        public static void Save(List<ConsoleInput> list)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(RegKey, false);
                using (RegistryKey root = Registry.CurrentUser.CreateSubKey(RegKey))
                    for (int i = 0; i < list.Count; i++)
                        using (RegistryKey k = root.CreateSubKey((i + 1).ToString()))
                        {
                            k.SetValue("Nom", list[i].Name);
                            k.SetValue("Ecran", list[i].Screen);
                            k.SetValue("Entree", list[i].Input);
                            k.SetValue("EntreePC", list[i].Home);
                            k.SetValue("Touche", (int)list[i].Key);
                            k.SetValue("Modificateurs", (int)list[i].Mods);
                            k.SetValue("Choisi", list[i].Chosen ? 1 : 0);
                        }
            }
            catch (Exception e) { Journal.Write("Consoles : réglages non enregistrés (" + e.Message + ")."); }
        }
    }

    // L'écran principal : celui de la barre des tâches, des nouvelles fenêtres et des notifications. Pendant le jeu sur
    // l'écran principal, son voisin le devient, comme avec « Faire de cet écran l'écran principal » dans Windows, qui
    // l'enregistre. Si Eclipse s'arrête pendant le jeu, une valeur dans le registre permet de tout remettre au
    // démarrage suivant (App.ResumeGame). Tout est lu à l'instant (Ddc.Monitors).
    static class PrimaryScreen
    {
        // Les structures de SetDisplayConfig (wingdi.h), à la taille exacte attendue par Windows : 72, 64 et 84 octets.
        [StructLayout(LayoutKind.Sequential)] internal struct LUID { public uint Low; public int High; }

        [StructLayout(LayoutKind.Sequential)] internal struct PathSource { public LUID Adapter; public uint Id, ModeIndex, Status; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PathTarget
        {
            public LUID Adapter;
            public uint Id, ModeIndex, Technology, Rotation, Scaling, RefreshNumerator, RefreshDenominator, ScanLine;
            public int Available;
            public uint Status;
        }

        [StructLayout(LayoutKind.Sequential)] internal struct PathInfo { public PathSource Source; public PathTarget Target; public uint Flags; }

        // Un mode de la disposition ; seuls les champs d'un mode « source » (la place d'un écran sur le bureau) sont nommés.
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        internal struct ModeInfo
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(28)] public int X;
            [FieldOffset(32)] public int Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct SourceName
        {
            public int Type, Size;
            public LUID Adapter;
            public uint Id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        }

        const uint OnlyActivePaths = 0x2, UseSupplied = 0x20, Apply = 0x80, SaveToDatabase = 0x200, AllowChanges = 0x400, SourceMode = 1;
        const int GetSourceName = 1, InsufficientBuffer = 122;
        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] ModeInfo[] modes, IntPtr topology);
        [DllImport("user32.dll")] static extern int SetDisplayConfig(uint numPaths, [In] PathInfo[] paths, uint numModes, [In] ModeInfo[] modes, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int DisplayConfigGetDeviceInfo(ref SourceName info);

        static bool Same(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        public static bool IsPrimary(string device)
        {
            return Ddc.Monitors().Exists(m => Same(m.Device, device) && (m.Flags & 1) != 0);
        }

        // Le voisin de cet écran (le plus proche, de centre à centre), qui sera principal pendant le jeu ; null s'il est seul.
        public static string Neighbour(string device)
        {
            List<Ddc.MONITORINFOEX> all = Ddc.Monitors();
            int me = all.FindIndex(m => Same(m.Device, device));
            if (me < 0) return null;
            string best = null;
            double nearest = double.MaxValue;
            foreach (Ddc.MONITORINFOEX m in all)
            {
                if (Same(m.Device, device)) continue;
                double dx = (m.Monitor.Left + m.Monitor.Right - all[me].Monitor.Left - all[me].Monitor.Right) / 2.0;
                double dy = (m.Monitor.Top + m.Monitor.Bottom - all[me].Monitor.Top - all[me].Monitor.Bottom) / 2.0;
                double d = dx * dx + dy * dy;
                if (d < nearest) { nearest = d; best = m.Device; }
            }
            return best;
        }

        // Fait de cet écran (\\.\DISPLAY1) l'écran principal : il passe en (0, 0) et les autres gardent leur place autour,
        // en un seul changement. SetDisplayConfig est la voie de la page Paramètres : ChangeDisplaySettingsEx refusait
        // (code -1) de remettre l'écran principal après un changement d'entrée de cet écran (essai du 17/11/2026).
        public static bool Set(string device)
        {
            if (IsPrimary(device)) return true;
            PathInfo[] paths = null;
            ModeInfo[] modes = null;
            uint np = 0, nm = 0;
            int read = InsufficientBuffer;
            for (int tries = 0; tries < 3 && read == InsufficientBuffer; tries++) // la disposition peut changer entre les deux appels
            {
                read = GetDisplayConfigBufferSizes(OnlyActivePaths, out np, out nm);
                if (read != 0) break;
                paths = new PathInfo[np];
                modes = new ModeInfo[nm];
                read = QueryDisplayConfig(OnlyActivePaths, ref np, paths, ref nm, modes, IntPtr.Zero);
            }
            if (read != 0) { Journal.Write("Écran principal : disposition des écrans illisible (" + read + ")."); return false; }
            int target = -1;
            for (int i = 0; i < np && target < 0; i++)
            {
                uint m = paths[i].Source.ModeIndex;
                if (m < nm && modes[m].Type == SourceMode && Same(SourceDevice(paths[i].Source), device)) target = (int)m;
            }
            if (target < 0) { Journal.Write("Écran principal : " + device + " introuvable."); return false; }
            int dx = modes[target].X, dy = modes[target].Y;
            for (int i = 0; i < nm; i++)
                if (modes[i].Type == SourceMode) { modes[i].X -= dx; modes[i].Y -= dy; }
            int result = SetDisplayConfig(np, paths, nm, modes, UseSupplied | Apply | SaveToDatabase | AllowChanges);
            if (result != 0) Journal.Write("Écran principal : Windows refuse le changement (" + result + ").");
            return result == 0;
        }

        // Le nom (\\.\DISPLAY1) de l'écran d'une source de la disposition, ou null.
        static string SourceDevice(PathSource source)
        {
            var name = new SourceName { Type = GetSourceName, Size = Marshal.SizeOf(typeof(SourceName)), Adapter = source.Adapter, Id = source.Id };
            return DisplayConfigGetDeviceInfo(ref name) == 0 ? name.Device : null;
        }

        // L'écran (\\.\DISPLAY1) qui porte cet identifiant, ou null s'il n'est pas branché.
        public static string DeviceOf(string id)
        {
            List<Ddc.MONITORINFOEX> all = Ddc.Monitors();
            List<string> ids = all.ConvertAll(m => Ddc.MonitorId(m.Device));
            int i = Ddc.FindScreen(ids, id);
            return i < 0 ? null : all[i].Device;
        }
    }

    // Le filtre de la lumière bleue : jamais, seulement sous le minimum de l'écran, ou tout le temps.
    enum BlueFilter { Non, SousLeMin, Toujours }

    // Sous le minimum de l'écran : l'image elle-même est assombrie, par l'effet de couleur plein écran de Windows (celui
    // de la loupe et des filtres de couleur). Pas de fenêtre posée par-dessus : rien ne cache, rien ne bloque les clics,
    // et l'effet disparaît de lui-même si Eclipse s'arrête, même brutalement. Il vaut pour tous les écrans à la fois.
    // Essais du 15/11 : la table de gamma de Windows refuse d'assombrir de plus de 50 % ; l'effet de couleur, lui, va
    // aussi bas qu'on veut. On s'arrête à 90 % (Max), pour que l'image ne disparaisse jamais tout à fait.
    // Ce n'est pas plus de lumière économisée : le rétroéclairage reste au minimum, l'image devient plus sombre.
    // Le même effet filtre la lumière bleue : une seule matrice porte l'assombrissement et la chaleur de l'image.
    // L'éclairage nocturne de Windows ne se commande pas depuis un programme (aucun moyen officiel) ; s'il est allumé,
    // les deux filtres s'additionnent.
    // Toujours sur le fil principal (celui de l'icône), le seul qui appelle la loupe.
    class Dimmer : IDisposable
    {
        public const int Max = 90;
        const int FullWarmthAt = 30; // « sous le minimum » : la chaleur choisie est atteinte à −30 %
        const string RegKey = @"Software\Eclipse";
        [DllImport("magnification.dll")] static extern bool MagInitialize();
        [DllImport("magnification.dll")] static extern bool MagUninitialize();
        [DllImport("magnification.dll")] static extern bool MagSetFullscreenColorEffect(float[] matrix);
        bool ready, failed, previewing;
        int level, warmth;
        int chaleur = 50;
        BlueFilter mode, usual; // usual : le choix d'avant Ctrl+Alt+Maj+B, jamais « Toujours »

        public Dimmer()
        {
            Load();
            if (mode == BlueFilter.Toujours) Apply(0);
        }

        public int Level { get { return level; } }     // 0 : rien ; 30 : l'image à 70 % de sa lumière
        public int Warmth { get { return warmth; } }   // la chaleur posée en ce moment, de 0 à 100 %
        public BlueFilter Mode { get { return mode; } }
        public int Chaleur { get { return chaleur; } } // la chaleur choisie, de 0 à 100 %

        public bool Set(int dim) { return Apply(dim); }

        public void SetMode(BlueFilter value)
        {
            mode = value;
            if (value != BlueFilter.Toujours) usual = value;
            Save();
            Apply(level);
        }

        // Enregistrée par Save, à la fermeture du panneau : pas d'écriture à chaque pas du curseur.
        public void SetChaleur(int value)
        {
            chaleur = Math.Max(0, Math.Min(100, value));
            Apply(level);
        }

        // Le temps de régler la chaleur, on la montre, même si le filtre attend d'être sous le minimum.
        public void Preview(bool on)
        {
            previewing = on;
            Apply(level);
        }

        // Ctrl+Alt+Maj+B : le filtre tout de suite, ou le retour au choix habituel.
        public void Toggle()
        {
            SetMode(Toggled(mode, usual));
        }

        public static BlueFilter Toggled(BlueFilter mode, BlueFilter usual)
        {
            return mode == BlueFilter.Toujours ? usual : BlueFilter.Toujours;
        }

        // La chaleur à poser (0 à 100 %) selon le choix, la chaleur choisie et l'assombrissement (0 à 90).
        public static int TargetWarmth(BlueFilter mode, int chaleur, int dim, bool previewing)
        {
            if (previewing || mode == BlueFilter.Toujours) return chaleur;
            if (mode == BlueFilter.SousLeMin) return chaleur * Math.Min(dim, FullWarmthAt) / FullWarmthAt;
            return 0;
        }

        bool Apply(int dim)
        {
            dim = Math.Max(0, Math.Min(Max, dim));
            int warm = TargetWarmth(mode, chaleur, dim, previewing);
            if (dim == level && warm == warmth) return true;
            if (!ready)
            {
                if (dim == 0 && warm == 0) return true;
                if (failed) return false;
                if (!MagInitialize()) { failed = true; Journal.Write("Effet de couleur impossible : la loupe de Windows refuse."); return false; }
                ready = true;
            }
            float k = 1f - dim / 100f, r, g, b;
            Tint(warm, out r, out g, out b);
            var m = new float[25];
            m[0] = k * r; m[6] = k * g; m[12] = k * b; // rouge, vert, bleu
            m[18] = m[24] = 1f;                         // transparence, et la ligne fixe
            if (!MagSetFullscreenColorEffect(m)) { Journal.Write("L'effet de couleur a été refusé par Windows."); return false; }
            level = dim;
            warmth = warm;
            if (dim == 0 && warm == 0) { MagUninitialize(); ready = false; }
            return true;
        }

        // La chaleur en couleur : de 6500 K (blanc) à 1900 K (plus de bleu du tout), en ligne droite sur l'échelle des
        // mireds (1 000 000 / K), plus proche de ce que l'œil perçoit. Les couleurs du corps noir viennent de
        // l'approximation de Tanner Helland, ramenées à 6500 K = blanc.
        static void Tint(int warm, out float r, out float g, out float b)
        {
            double mired = 1e6 / 6500 + (1e6 / 1900 - 1e6 / 6500) * warm / 100.0, r0, g0, b0, r1, g1, b1;
            Blackbody(6500, out r0, out g0, out b0);
            Blackbody(1e6 / mired, out r1, out g1, out b1);
            r = (float)Math.Min(1, r1 / r0);
            g = (float)Math.Min(1, g1 / g0);
            b = (float)Math.Min(1, b1 / b0);
        }

        static void Blackbody(double kelvin, out double r, out double g, out double b)
        {
            double t = kelvin / 100;
            r = t <= 66 ? 255 : 329.698727446 * Math.Pow(t - 60, -0.1332047592);
            g = t <= 66 ? 99.4708025861 * Math.Log(t) - 161.1195681661 : 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
            b = t >= 66 ? 255 : t <= 19 ? 0 : 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
            r = Math.Max(0, Math.Min(255, r));
            g = Math.Max(0, Math.Min(255, g));
            b = Math.Max(0, Math.Min(255, b));
        }

        void Load()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    if (key == null) return;
                    mode = (BlueFilter)ReadInt(key, "FiltreBleu", 0, 0, 2);
                    usual = (BlueFilter)ReadInt(key, "FiltreBleuHabituel", 0, 0, 1);
                    chaleur = ReadInt(key, "Chaleur", chaleur, 0, 100);
                }
            }
            catch (Exception e) { Journal.Write("Lumière bleue : réglages illisibles (" + e.Message + ")."); }
        }

        static int ReadInt(RegistryKey key, string name, int fallback, int min, int max)
        {
            object v = key.GetValue(name);
            return v is int ? Math.Max(min, Math.Min(max, (int)v)) : fallback;
        }

        public void Save()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKey))
                {
                    key.SetValue("FiltreBleu", (int)mode);
                    key.SetValue("FiltreBleuHabituel", (int)usual);
                    key.SetValue("Chaleur", chaleur);
                }
            }
            catch (Exception e) { Journal.Write("Lumière bleue : réglages non enregistrés (" + e.Message + ")."); }
        }

        // L'image revient telle quelle ; le choix enregistré, lui, ne change pas.
        public void Dispose()
        {
            mode = BlueFilter.Non;
            previewing = false;
            Apply(0);
        }
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
        Color accent = Fill;
        string message;
        public event EventHandler ValueChanged; // seulement quand c'est vous qui le bougez

        public Color Accent { get { return accent; } set { accent = value; Invalidate(); } }

        // Le texte montré à la place de la barre quand le curseur est grisé : un écran qui ne répond pas.
        public string Message
        {
            get { return message; }
            set { message = value; AccessibleDescription = value; Invalidate(); }
        }

        public int Minimum
        {
            get { return minimum; }
            set { minimum = Math.Min(0, value); Value = this.value; Invalidate(); }
        }

        // Le dessin du curseur, partagé avec le rappel en bas de l'écran.
        public static void PaintBar(Graphics g, RectangleF area, int minimum, int value, float thick, bool enabled, Font small, bool showMin, Color accent)
        {
            float cy = area.Top + area.Height / 2f;
            Func<int, float> at = v => area.Left + area.Width * (v - minimum) / (float)(100 - minimum);
            float zero = at(0), x = at(value);
            using (var track = new Pen(Track, thick) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(track, zero, cy, area.Right, cy);
            if (minimum < 0)
                using (var below = new Pen(BelowTrack, thick) { StartCap = LineCap.Round, EndCap = LineCap.Flat })
                    g.DrawLine(below, area.Left, cy, zero, cy);
            Color fill = !enabled ? Track : value < 0 ? Below : accent;
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
            AccessibleRole = AccessibleRole.Slider;
        }

        public int Value
        {
            get { return value; }
            set
            {
                int v = Math.Max(minimum, Math.Min(100, value));
                if (v == this.value) return;
                this.value = v;
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            }
        }

        // Pour un lecteur d'écran : « 40 % », ou « −30 % » sous le minimum.
        protected override AccessibleObject CreateAccessibilityInstance() { return new Access(this); }

        class Access : ControlAccessibleObject
        {
            readonly Slider owner;
            public Access(Slider owner) : base(owner) { this.owner = owner; }
            public override string Value { get { int v = owner.Value; return (v < 0 ? "−" + (-v) : v.ToString()) + " %"; } }
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
            g.Clear(BackColor); // celui du parent, sauf si le panneau en donne un autre (une carte)
            int r = Radius, cy = BarHeight / 2, w = Width - 2 * r, thick = Math.Max(3, BarHeight / 8);
            if (!Enabled && !string.IsNullOrEmpty(message))
            {
                using (var f = new Font(Ui.TextFont, 9f))
                    TextRenderer.DrawText(g, message, f, new Rectangle(r, 0, w, BarHeight), Ui.Dim, BackColor,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                return;
            }
            using (var small = new Font(Ui.TextFont, 7.5f))
                PaintBar(g, new RectangleF(r, 0, w, BarHeight), minimum, value, thick, Enabled, small, true, accent);
            float x = r + w * (value - minimum) / (float)(100 - minimum);
            Color fill = !Enabled ? Track : value < 0 ? Below : accent;
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

    // ---- Le style des panneaux : à la Windows 11, en sombre ----

    static class Ui
    {
        public static readonly Color Back = Color.FromArgb(32, 32, 32);   // le fond des panneaux
        public static readonly Color Card = Color.FromArgb(43, 43, 43);   // le fond d'une carte
        public static readonly Color Line = Color.FromArgb(60, 60, 60);   // les traits et les bordures
        public static readonly Color Text = Color.White;
        public static readonly Color Dim = Color.FromArgb(170, 170, 170); // le texte secondaire
        public static readonly Color Button = Color.FromArgb(50, 50, 50);
        public static readonly Color Hover = Color.FromArgb(64, 64, 64);
        public static readonly Color Pressed = Color.FromArgb(40, 40, 40);
        public static readonly Color Chosen = Color.FromArgb(84, 84, 84); // une pastille grise choisie (« Non »)
        public static readonly Color Warm = Color.FromArgb(255, 183, 77); // la chaleur du filtre, ambre
        // Les icônes viennent de la police d'icônes de Windows : Segoe Fluent Icons (Windows 11), sinon Segoe MDL2 Assets.
        public const string Sun = "", Moon = "", Power = "", Game = "";
        public static readonly string TextFont = Pick("Segoe UI Variable Text", "Segoe UI");
        public static readonly string StrongFont = Pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold");
        public static readonly string IconFont = Pick("Segoe Fluent Icons", "Segoe MDL2 Assets");

        // Une police absente est remplacée sans prévenir par une autre : on vérifie le nom obtenu.
        static string Pick(string wanted, string fallback)
        {
            try { using (var f = new Font(wanted, 10f)) return f.Name == wanted ? wanted : fallback; }
            catch (ArgumentException) { return fallback; }
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(2 * radius, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // La barre de titre en sombre (Windows 10 2004 et plus : 20 ; avant : 19).
        public static void DarkTitle(IntPtr hwnd)
        {
            try
            {
                int dark = 1;
                if (Ddc.DwmSetWindowAttribute(hwnd, 20, ref dark, 4) != 0) Ddc.DwmSetWindowAttribute(hwnd, 19, ref dark, 4);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        // Le contour du focus, seulement quand on se déplace au clavier (Tab).
        public static void FocusRing(Graphics g, RectangleF r, float radius)
        {
            using (GraphicsPath path = Round(r, radius))
            using (var pen = new Pen(Color.White, 1.5f))
                g.DrawPath(pen, path);
        }

        // La place d'un écran parmi count, de gauche à droite : « à gauche, principal », « au milieu »... ; vide s'il est seul.
        public static string Place(int i, int count, bool primary)
        {
            string where = count == 1 ? string.Empty : i == 0 ? "à gauche" : i == count - 1 ? "à droite" : "au milieu";
            if (primary) where = where.Length == 0 ? "principal" : where + ", principal";
            return where;
        }
    }

    // Un interrupteur à la Windows 11 : un clic, ou Espace. Son texte est dans une étiquette à côté, qui le bascule aussi.
    class Toggle : Control
    {
        bool on, hover;
        public event EventHandler CheckedChanged; // seulement quand c'est vous qui le basculez

        public Toggle()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            SetStyle(ControlStyles.StandardDoubleClick, false); // deux clics rapides basculent deux fois
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.CheckButton;
        }

        public bool Checked
        {
            get { return on; }
            set
            {
                if (value == on) return;
                on = value;
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            }
        }

        public void Flip()
        {
            Checked = !on;
            if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            Flip();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space) { Flip(); e.Handled = true; }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float h = Math.Min(Height - 6f, Width / 2f - 3f), w = 2 * h, x = Width - w - 3, y = (Height - h) / 2f;
            var track = new RectangleF(x, y, w, h);
            Color off = hover ? Color.FromArgb(220, 220, 220) : Color.FromArgb(165, 165, 165);
            using (GraphicsPath path = Ui.Round(track, h / 2))
            {
                if (on) using (var brush = new SolidBrush(Enabled ? Slider.Fill : Slider.Track)) g.FillPath(brush, path);
                else using (var pen = new Pen(off, Math.Max(1f, h / 16))) g.DrawPath(pen, path);
            }
            float d = h * (hover ? 0.62f : 0.52f), cx = on ? x + w - h / 2 : x + h / 2;
            using (var knob = new SolidBrush(on ? Color.Black : off))
                g.FillEllipse(knob, cx - d / 2, y + (h - d) / 2, d, d);
            if (Focused && ShowFocusCues) Ui.FocusRing(g, RectangleF.Inflate(track, 2.5f, 2.5f), h / 2 + 2.5f);
        }

        protected override AccessibleObject CreateAccessibilityInstance() { return new Access(this); }

        class Access : ControlAccessibleObject
        {
            readonly Toggle owner;
            public Access(Toggle owner) : base(owner) { this.owner = owner; }
            public override AccessibleStates State { get { return owner.on ? base.State | AccessibleStates.Checked : base.State; } }
            public override string DefaultAction { get { return owner.on ? "Désactiver" : "Activer"; } }
            public override void DoDefaultAction() { owner.Flip(); }
        }
    }

    // Un choix parmi quelques pastilles côte à côte : un clic, ou les flèches gauche et droite. La première pastille
    // (« Non ») reste grise quand elle est choisie ; les autres prennent la couleur Accent.
    class Choice : Control
    {
        readonly string[] items;
        int selected, hover = -1;
        Color accent = Slider.Fill;
        public event EventHandler SelectedChanged; // seulement quand c'est vous qui choisissez

        public Choice(params string[] items)
        {
            this.items = items;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.Grouping;
        }

        public Color Accent { get { return accent; } set { accent = value; Invalidate(); } }

        public int Selected
        {
            get { return selected; }
            set
            {
                int v = Math.Max(0, Math.Min(items.Length - 1, value));
                if (v == selected) return;
                selected = v;
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.Selection, v);
            }
        }

        void Pick(int i)
        {
            i = Math.Max(0, Math.Min(items.Length - 1, i));
            if (i == selected) return;
            Selected = i;
            if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
        }

        RectangleF Segment(int i)
        {
            float pad = Height / 10f, w = (Width - 2 * pad) / items.Length;
            return new RectangleF(pad + i * w, pad, w, Height - 2 * pad);
        }

        int At(int x)
        {
            for (int i = 0; i < items.Length - 1; i++) if (x < Segment(i).Right) return i;
            return items.Length - 1;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            Pick(At(e.X));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = At(e.X);
            if (h != hover) { hover = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override bool IsInputKey(Keys key)
        {
            return key == Keys.Left || key == Keys.Right || base.IsInputKey(key);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left) Pick(selected - 1);
            else if (e.KeyCode == Keys.Right) Pick(selected + 1);
            else if (e.KeyCode == Keys.Home) Pick(0);
            else if (e.KeyCode == Keys.End) Pick(items.Length - 1);
            else return;
            e.Handled = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float radius = Height / 5f, inner = radius - Height / 10f;
            using (GraphicsPath path = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), radius))
            using (var brush = new SolidBrush(Ui.Button))
                g.FillPath(brush, path);
            for (int i = 0; i < items.Length; i++)
            {
                RectangleF r = Segment(i);
                // Grisé (l'écran des consoles débranché) : ni couleur, ni survol.
                Color back = i == selected ? (i == 0 || !Enabled ? Ui.Chosen : accent) : i == hover && Enabled ? Ui.Hover : Color.Empty;
                if (!back.IsEmpty)
                    using (GraphicsPath p = Ui.Round(r, inner))
                    using (var b = new SolidBrush(back))
                        g.FillPath(b, p);
                Color fore = i != selected || !Enabled ? Ui.Dim : i == 0 ? Ui.Text : Color.Black;
                TextRenderer.DrawText(g, items[i], Font, Rectangle.Round(r), fore, back.IsEmpty ? Ui.Button : back,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
            if (Focused && ShowFocusCues) Ui.FocusRing(g, RectangleF.Inflate(Segment(selected), -1.5f, -1.5f), inner);
        }

        // Pour un lecteur d'écran : un groupe de boutons radio.
        protected override AccessibleObject CreateAccessibilityInstance() { return new Access(this); }

        class Access : ControlAccessibleObject
        {
            readonly Choice owner;
            public Access(Choice owner) : base(owner) { this.owner = owner; }
            public override string Value { get { return owner.items[owner.selected]; } }
            public override int GetChildCount() { return owner.items.Length; }
            public override AccessibleObject GetChild(int index)
            {
                return index >= 0 && index < owner.items.Length ? new Item(owner, this, index) : null;
            }
        }

        class Item : AccessibleObject
        {
            readonly Choice owner;
            readonly AccessibleObject parent;
            readonly int index;
            public Item(Choice owner, AccessibleObject parent, int index) { this.owner = owner; this.parent = parent; this.index = index; }
            public override string Name { get { return owner.items[index]; } }
            public override AccessibleRole Role { get { return AccessibleRole.RadioButton; } }
            public override AccessibleObject Parent { get { return parent; } }
            public override Rectangle Bounds { get { return owner.RectangleToScreen(Rectangle.Round(owner.Segment(index))); } }
            public override string DefaultAction { get { return "Choisir"; } }
            public override void DoDefaultAction() { owner.Pick(index); }
            public override AccessibleStates State
            {
                get
                {
                    AccessibleStates s = AccessibleStates.Selectable | AccessibleStates.Focusable;
                    return index == owner.selected ? s | AccessibleStates.Checked | AccessibleStates.Selected : s;
                }
            }
        }
    }

    // Un bouton arrondi, avec ou sans icône : un clic, Entrée ou Espace. Primary : le bouton principal d'une fenêtre,
    // en couleur.
    class RoundButton : Control
    {
        readonly string glyph;
        bool hover, down;
        public bool Primary;

        public RoundButton(string glyph)
        {
            this.glyph = glyph;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.PushButton;
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = down = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { down = true; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            down = false;
            Invalidate();
        }

        protected override bool IsInputKey(Keys key) { return key == Keys.Enter || base.IsInputKey(key); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode != Keys.Enter && e.KeyCode != Keys.Space) return;
            e.Handled = true;
            OnClick(EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float radius = Height / 7f;
            var r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            bool live = Enabled && (hover || down);
            Color back = Primary ? (!Enabled ? Ui.Button : down ? Color.FromArgb(64, 156, 204) : live ? Color.FromArgb(71, 177, 232) : Slider.Fill)
                : down ? Ui.Pressed : live ? Ui.Hover : Ui.Button;
            Color fore = !Enabled ? Ui.Dim : Primary ? Color.Black : Ui.Text;
            using (GraphicsPath path = Ui.Round(r, radius))
            {
                using (var brush = new SolidBrush(back)) g.FillPath(brush, path);
                if (!Primary || !Enabled) using (var pen = new Pen(Ui.Line)) g.DrawPath(pen, path);
            }
            const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            using (var icon = new Font(Ui.IconFont, Font.Size))
            {
                Size it = glyph.Length == 0 ? Size.Empty : TextRenderer.MeasureText(g, glyph, icon, Size.Empty, flags);
                Size tt = TextRenderer.MeasureText(g, Text, Font, Size.Empty, flags);
                int gap = glyph.Length == 0 ? 0 : Font.Height / 2, x = (Width - it.Width - gap - tt.Width) / 2;
                if (glyph.Length > 0) TextRenderer.DrawText(g, glyph, icon, new Rectangle(x, 0, it.Width, Height), fore, back, flags);
                TextRenderer.DrawText(g, Text, Font, new Rectangle(x + it.Width + gap, 0, tt.Width + 2, Height), fore, back, flags);
            }
            if (Focused && ShowFocusCues) Ui.FocusRing(g, RectangleF.Inflate(r, -2.5f, -2.5f), radius - 2);
        }
    }

    // Le panneau qui s'ouvre d'un clic sur l'icône, près de l'heure : la luminosité (un curseur par écran, de gauche à
    // droite), le filtre de la lumière bleue, « Lier les écrans » et l'extinction, chaque partie dans une carte.
    // À la Windows 11, en sombre, avec un fondu à l'ouverture. Les mesures sont en pixels à 96 ppp ; S() les met à
    // l'échelle de l'écran.
    class BrightnessPanel : Form
    {
        const string LinkValue = "LierLuminosite";
        const int W = 360, Edge = 10, Pad = 22; // la largeur ; le bord des cartes ; le bord de ce qu'elles contiennent
        const int Inner = W - 2 * Pad;
        readonly Brightness light;
        readonly Dimmer dimmer;
        readonly float scale;
        readonly List<Slider> sliders = new List<Slider>();
        readonly List<Label> values = new List<Label>();
        readonly List<string> devices = new List<string>();
        readonly Toggle link = new Toggle();
        readonly ToolTip tips = new ToolTip();
        readonly Choice filter = new Choice("Non", "Sous le min", "Toujours");
        readonly Slider warmth = new Slider();
        readonly Label warmthValue = new Label();
        readonly Timer fade = new Timer { Interval = 15 };
        readonly Timer previewEnd = new Timer { Interval = 1200 };
        readonly List<Rectangle> cards = new List<Rectangle>(); // en pixels de l'écran
        Color surface = Ui.Back; // le fond derrière les contrôles qu'on pose
        int y, cardTop;          // en pixels à 96 ppp
        int targetTop, fadeStep;
        bool roundedByWindows;

        // pick(écran, console) : passer cet écran sur la console, ou sur le PC si console vaut null.
        public BrightnessPanel(Brightness light, Dimmer dimmer, string offText, Action turnOff,
            List<ConsoleInput> consoles, ConsoleInput playing, Action<string, ConsoleInput> pick)
        {
            this.light = light;
            this.dimmer = dimmer;
            Text = "Eclipse : luminosité";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            BackColor = Ui.Back;
            ForeColor = Ui.Text;
            Font = new Font(Ui.TextFont, 10f);
            Opacity = 0; // le fondu à l'ouverture
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;

            var screensNow = new List<Screen>(Screen.AllScreens);
            screensNow.Sort((a, b) => a.Bounds.X != b.Bounds.X ? a.Bounds.X.CompareTo(b.Bounds.X) : a.Bounds.Y.CompareTo(b.Bounds.Y));
            y = 14;
            Heading(Ui.Sun, "Luminosité");
            BeginCard();
            for (int i = 0; i < screensNow.Count; i++)
            {
                Screen sc = screensNow[i];
                string where = Ui.Place(i, screensNow.Count, sc.Primary);
                string name = light.NameOf(sc.DeviceName) ?? ("Écran " + (i + 1));
                Add(new Label { Text = name + (where.Length > 0 ? "  ·  " + where : string.Empty), AutoSize = false, AutoEllipsis = true, UseMnemonic = false },
                    Pad, y, Inner - 64, 22);
                var value = new Label { Text = "…", TextAlign = ContentAlignment.MiddleRight, ForeColor = Ui.Dim, AutoSize = false };
                Add(value, W - Pad - 64, y, 64, 22);
                var slider = new Slider { Enabled = false, Minimum = -Dimmer.Max, AccessibleName = "Luminosité de " + name };
                Add(slider, Pad - 6, y + 24, Inner + 12, 42);
                int index = i;
                slider.ValueChanged += delegate { OnSlide(index); };
                sliders.Add(slider);
                values.Add(value);
                devices.Add(sc.DeviceName);
                y += 70;
            }
            EndCard();

            Heading(Ui.Moon, "Lumière bleue");
            BeginCard();
            filter.Accent = Ui.Warm;
            filter.AccessibleName = "Filtre de la lumière bleue";
            filter.AccessibleDescription = "Moins de bleu : une image plus chaude, sur tous les écrans à la fois.\n"
                + "Sans effet sur les consoles ni sur les jeux en plein écran exclusif.\n"
                + "S'ajoute à l'éclairage nocturne de Windows, s'il est allumé.";
            tips.SetToolTip(filter, filter.AccessibleDescription);
            filter.SelectedChanged += delegate { dimmer.SetMode((BlueFilter)filter.Selected); ShowFilter(); };
            Add(filter, Pad, y, Inner, 32);
            y += 42;
            Add(new Label { Text = "Chaleur", ForeColor = Ui.Dim, AutoSize = false }, Pad, y, 120, 22);
            warmthValue.TextAlign = ContentAlignment.MiddleRight;
            warmthValue.ForeColor = Ui.Dim;
            warmthValue.AutoSize = false;
            Add(warmthValue, W - Pad - 64, y, 64, 22);
            warmth.Accent = Ui.Warm;
            warmth.AccessibleName = "Chaleur du filtre";
            warmth.ValueChanged += delegate { OnWarmth(); };
            Add(warmth, Pad - 6, y + 22, Inner + 12, 28);
            y += 54;
            EndCard();

            if (screensNow.Count >= 2)
            {
                BeginCard();
                var linkLabel = new Label { Text = "Lier les écrans", AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Cursor = Cursors.Hand };
                linkLabel.Click += delegate { link.Focus(); link.Flip(); };
                Add(linkLabel, Pad, y, Inner - 56, 30);
                link.Checked = LoadLink();
                link.AccessibleName = "Lier les écrans";
                link.CheckedChanged += delegate { SaveLink(link.Checked); };
                Add(link, W - Pad - 48, y, 48, 30);
                y += 30;
                EndCard();
            }
            if (consoles.Count > 0) AddConsoles(consoles, playing, pick);
            y += 4;
            var off = new RoundButton(Ui.Power) { Text = offText };
            off.Click += delegate { Close(); turnOff(); };
            surface = Ui.Back;
            Add(off, Edge, y, W - 2 * Edge, 36);
            y += 36 + 16;
            ClientSize = new Size(S(W), S(y));
            ShowFilter();

            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            Deactivate += delegate { Close(); };
            fade.Tick += delegate { FadeStep(); };
            previewEnd.Tick += delegate { previewEnd.Stop(); dimmer.Preview(false); };
            FormClosed += delegate
            {
                fade.Stop();
                previewEnd.Stop();
                dimmer.Preview(false);
                dimmer.Save();
            };
        }

        int S(int v) { return (int)Math.Round(v * scale); }

        void Add(Control c, int x, int top, int w, int h)
        {
            c.SetBounds(S(x), S(top), S(w), S(h));
            c.BackColor = surface;
            Controls.Add(c);
        }

        // Les consoles : pour chaque écran qui en a, « PC » et ses consoles en pastilles ; celle à l'écran est choisie.
        // Un choix ferme le panneau, puis l'écran change.
        void AddConsoles(List<ConsoleInput> consoles, ConsoleInput playing, Action<string, ConsoleInput> pick)
        {
            Heading(Ui.Game, "Consoles");
            BeginCard();
            var screens = new List<string>();
            foreach (ConsoleInput c in consoles)
                if (!screens.Exists(s => string.Equals(s, c.Screen, StringComparison.OrdinalIgnoreCase))) screens.Add(c.Screen);
            foreach (string screen in screens)
            {
                string id = screen;
                List<ConsoleInput> group = consoles.FindAll(c => string.Equals(c.Screen, id, StringComparison.OrdinalIgnoreCase));
                string device = PrimaryScreen.DeviceOf(id);
                string name = device == null ? null : light.NameOf(device);
                if (screens.Count > 1)
                {
                    Add(new Label { Text = name ?? "Écran débranché", AutoSize = false, AutoEllipsis = true, UseMnemonic = false }, Pad, y, Inner, 22);
                    y += 26;
                }
                var names = new List<string> { "PC" };
                var keys = new StringBuilder();
                foreach (ConsoleInput c in group)
                {
                    names.Add(c.Name);
                    if (c.WorkingShortcut != null) keys.Append(c.WorkingShortcut + " : " + c.Name + "\n");
                }
                var choice = new Choice(names.ToArray());
                choice.AccessibleName = "Ce que montre l'écran " + (name ?? string.Empty);
                choice.AccessibleDescription = keys + "Le même raccourci ramène l'écran sur le PC.";
                tips.SetToolTip(choice, choice.AccessibleDescription);
                choice.Selected = group.IndexOf(playing) + 1; // 0, « PC », si aucune console de cet écran n'est à l'écran
                choice.Enabled = device != null;
                choice.SelectedChanged += delegate
                {
                    int k = choice.Selected;
                    Close();
                    pick(id, k == 0 ? null : group[k - 1]);
                };
                Add(choice, Pad, y, Inner, 32);
                y += 42;
            }
            y -= 10;
            EndCard();
        }

        void Heading(string glyph, string text)
        {
            Add(new Label { Text = glyph, Font = new Font(Ui.IconFont, 11f), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false },
                Edge, y, 24, 24);
            Add(new Label { Text = text, Font = new Font(Ui.StrongFont, 10.5f), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft },
                Edge + 26, y, W - 2 * Edge - 26, 24);
            y += 32;
        }

        // Une carte : un fond plus clair, arrondi, dessiné par OnPaint derrière ce qu'on pose entre BeginCard et EndCard.
        void BeginCard()
        {
            cardTop = y;
            y += 12;
            surface = Ui.Card;
        }

        void EndCard()
        {
            y += 8;
            cards.Add(new Rectangle(S(Edge), S(cardTop), S(W - 2 * Edge), S(y - cardTop)));
            y += 12;
            surface = Ui.Back;
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
            try
            {
                int round = 2; // coins arrondis : Windows 11 seulement ; sous Windows 10, le cadre est dessiné par OnPaint
                roundedByWindows = Ddc.DwmSetWindowAttribute(Handle, 33, ref round, 4) == 0;
                int border = ColorTranslator.ToWin32(Ui.Line);
                if (roundedByWindows) Ddc.DwmSetWindowAttribute(Handle, 34, ref border, 4);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
            int gap = S(12);
            targetTop = work.Bottom - Height - gap;
            Location = new Point(work.Right - Width - gap, targetTop + S(8));
            fade.Start();
            light.Refresh(Fill);
        }

        // Le fondu : 8 pas d'environ 15 ms, qui finissent en douceur, en montant de 8 pixels.
        void FadeStep()
        {
            fadeStep++;
            double t = Math.Min(1, fadeStep / 8.0), ease = 1 - Math.Pow(1 - t, 3);
            Opacity = ease;
            Top = targetTop + (int)Math.Round(S(8) * (1 - ease));
            if (t < 1) return;
            fade.Stop();
            Opacity = 1;
            Top = targetTop;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(Ui.Card))
                foreach (Rectangle c in cards)
                    using (GraphicsPath path = Ui.Round(c, S(8)))
                        g.FillPath(brush, path);
            if (roundedByWindows) return;
            g.SmoothingMode = SmoothingMode.None;
            using (var pen = new Pen(Ui.Line))
                g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
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
            for (int i = 0; i < devices.Count; i++)
            {
                ScreenLight s = read.Find(x => string.Equals(x.Device, devices[i], StringComparison.OrdinalIgnoreCase));
                bool usable = s != null && s.Ok;
                sliders[i].Enabled = usable;
                sliders[i].Message = usable ? null : "Ne répond pas : activez DDC/CI dans son menu.";
                if (usable) ShowValue(i, dimmer.Level > 0 ? -dimmer.Level : s.Percent);
                else values[i].Text = "—";
            }
        }

        // Une valeur sur le curseur et dans le texte : « 40 % », ou en orange « −30 % » sous le minimum.
        void ShowValue(int i, int v)
        {
            sliders[i].Value = v;
            values[i].Text = v < 0 ? "−" + (-v) + " %" : v + " %";
            values[i].ForeColor = v < 0 ? Slider.Below : Ui.Dim;
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
                    ShowValue(i, v);
                    light.Set(devices[i], 0);
                }
                dimmer.Set(-v);
                return;
            }
            bool wasDim = dimmer.Level > 0;
            if (wasDim) dimmer.Set(0);
            for (int i = 0; i < sliders.Count; i++)
            {
                if (!sliders[i].Enabled) continue;
                bool follow = i == index || link.Checked;
                if (!follow && !wasDim) continue;
                ShowValue(i, follow ? v : 0); // non liés : remontés au seuil, là où ils étaient vraiment
                light.Set(devices[i], follow ? v : 0);
            }
        }

        // Le filtre tel qu'il est réglé ; aussi appelé quand Ctrl+Alt+Maj+B le change, panneau ouvert.
        public void ShowFilter()
        {
            filter.Selected = (int)dimmer.Mode;
            warmth.Value = dimmer.Chaleur;
            warmth.Enabled = dimmer.Mode != BlueFilter.Non;
            warmthValue.Text = dimmer.Chaleur + " %";
        }

        // La chaleur se voit pendant qu'on la règle, même si le filtre attend d'être sous le minimum.
        void OnWarmth()
        {
            dimmer.SetChaleur(warmth.Value);
            warmthValue.Text = warmth.Value + " %";
            if (dimmer.Mode != BlueFilter.SousLeMin) return;
            dimmer.Preview(true);
            previewEnd.Stop();
            previewEnd.Start();
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

        protected override void Dispose(bool disposing)
        {
            if (disposing) { fade.Dispose(); previewEnd.Dispose(); tips.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // Le rappel en bas de l'écran, 1,5 s, sans prendre le clavier : « Luminosité 60 % » après Ctrl+Alt+Maj+Haut / Bas,
    // l'état du filtre après Ctrl+Alt+Maj+B, ou un message des consoles (3 s).
    class LightOsd : Form
    {
        readonly Timer hide = new Timer { Interval = 1500 };
        int percent;
        string filterText; // le rappel du filtre ; null pour celui de la luminosité
        string message;    // un message des consoles, sur une ligne

        public LightOsd()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Ui.Back;
            ForeColor = Ui.Text;
            Font = new Font(Ui.TextFont, 10f);
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
            filterText = message = null;
            percent = value;
            Pop(300, null, 1500);
        }

        public void FlashFilter(BlueFilter mode, int chaleur)
        {
            message = null;
            filterText = mode == BlueFilter.Toujours ? "Lumière bleue filtrée  " + chaleur + " %"
                : mode == BlueFilter.SousLeMin ? "Lumière bleue : filtrée sous le minimum"
                : "Lumière bleue : filtre arrêté";
            percent = mode == BlueFilter.Toujours ? chaleur : -1;
            Pop(300, null, 1500);
        }

        // Un message des consoles, 3 s, jamais sur l'écran notOn (\\.\DISPLAY1), celui qui montre la console.
        public void FlashMessage(string text, string notOn)
        {
            filterText = null;
            message = text;
            Pop(0, notOn, 3000);
        }

        // width : en pixels à 96 ppp ; 0 pour la largeur du message.
        void Pop(int width, string notOn, int ms)
        {
            float scale;
            int textWidth = 0;
            using (Graphics g = CreateGraphics())
            {
                scale = g.DpiX / 96f;
                if (message != null) textWidth = TextRenderer.MeasureText(g, message, Font).Width;
            }
            Rectangle work = WorkArea(notOn);
            int w = width > 0 ? (int)(width * scale) : textWidth + (int)(68 * scale);
            Size = new Size(Math.Min(w, work.Width - (int)(32 * scale)), (int)(64 * scale));
            Location = new Point(work.Left + (work.Width - Width) / 2, work.Bottom - Height - (int)(48 * scale));
            if (!Visible) { Show(); try { int round = 2; Ddc.DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { } }
            Invalidate();
            hide.Stop();
            hide.Interval = ms;
            hide.Start();
        }

        // La zone de l'écran de la souris ; si c'est notOn, celle de l'écran principal, ou d'un autre. Lue à l'instant :
        // juste après un changement d'écran principal, Screen.AllScreens n'est pas encore à jour.
        static Rectangle WorkArea(string notOn)
        {
            List<Ddc.MONITORINFOEX> all = Ddc.Monitors();
            Point p = Cursor.Position;
            int pick = -1;
            for (int i = 0; i < all.Count; i++)
            {
                if (notOn != null && string.Equals(all[i].Device, notOn, StringComparison.OrdinalIgnoreCase)) continue;
                Ddc.RECT r = all[i].Monitor;
                if (p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom) { pick = i; break; }
                if (pick < 0 || (all[i].Flags & 1) != 0) pick = i;
            }
            if (pick < 0) return Screen.FromPoint(p).WorkingArea;
            Ddc.RECT w = all[pick].Work;
            return Rectangle.FromLTRB(w.Left, w.Top, w.Right, w.Bottom);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float s = Height / 64f;
            if (message != null)
            {
                using (var icon = new Font(Ui.IconFont, 13f))
                    TextRenderer.DrawText(g, Ui.Game, icon, new Rectangle((int)(16 * s), 0, (int)(28 * s), Height), Slider.Fill, BackColor,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                TextRenderer.DrawText(g, message, Font, new Rectangle((int)(52 * s), 0, Width - (int)(60 * s), Height), ForeColor, BackColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                return;
            }
            var bar = new RectangleF(16 * s, 30 * s, Width - 32 * s, 14 * s);
            if (filterText != null)
            {
                bool on = percent >= 0;
                float top = on ? 8 * s : (Height - Font.GetHeight(g)) / 2;
                using (var brush = new SolidBrush(on ? Ui.Warm : ForeColor))
                    g.DrawString(filterText, Font, brush, 16 * s, top);
                if (on) Slider.PaintBar(g, bar, 0, percent, 4 * s, true, null, false, Ui.Warm);
                return;
            }
            string text = percent == NoScreen ? "Luminosité : aucun écran ne répond"
                : percent < 0 ? "Sous le minimum  −" + (-percent) + " %"
                : "Luminosité  " + percent + " %";
            using (var brush = new SolidBrush(percent < 0 && percent != NoScreen ? Slider.Below : ForeColor))
                g.DrawString(text, Font, brush, 16 * s, 8 * s);
            if (percent == NoScreen) return;
            using (var small = new Font(Ui.TextFont, 7.5f))
                Slider.PaintBar(g, bar, -Dimmer.Max, percent, 4 * s, true, small, true, Slider.Fill);
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

    // ---- Le choix d'un raccourci ----

    // Demander une combinaison à la personne ; false si elle renonce. refusal : pourquoi une combinaison ne peut pas
    // servir, ou null.
    delegate bool CaptureKeys(IWin32Window owner, string title, Func<uint, uint, string> refusal, out uint mods, out uint key);

    // La fenêtre où l'on fait sa combinaison. Une combinaison refusée affiche sa raison, et on en fait une autre ;
    // Échap ou la croix : rien ne change. En sombre, comme le panneau.
    class HotkeyDialog : Form
    {
        const string Ask = "Faites votre combinaison : au moins deux touches parmi Ctrl, Alt et Maj, plus une autre touche. Échap : annuler.";
        readonly Label label = new Label { Text = Ask, UseMnemonic = false };
        readonly Func<uint, uint, string> refusal;
        public uint Mods, Key;

        public HotkeyDialog(string title, Func<uint, uint, string> refusal)
        {
            this.refusal = refusal;
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            TopMost = true;
            KeyPreview = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(420, 100);
            BackColor = Ui.Back;
            ForeColor = Ui.Text;
            Font = new Font(Ui.TextFont, 10f);
            label.SetBounds(16, 16, 388, 68);
            Controls.Add(label);
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            KeyDown += OnCapture;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Ui.DarkTitle(Handle);
        }

        void OnCapture(object sender, KeyEventArgs e)
        {
            e.SuppressKeyPress = e.Handled = true;
            Keys key = e.KeyCode;
            if (key == Keys.Escape && e.Modifiers == Keys.None) { DialogResult = DialogResult.Cancel; return; }
            if (key == Keys.ControlKey || key == Keys.Menu || key == Keys.ShiftKey || key == Keys.LWin || key == Keys.RWin) return;
            if (key == Keys.Delete) return; // Ctrl+Alt+Suppr appartient à Windows
            uint mods = 0;
            int count = 0;
            if (e.Control) { mods |= Native.MOD_CONTROL; count++; }
            if (e.Alt) { mods |= Native.MOD_ALT; count++; }
            if (e.Shift) { mods |= Native.MOD_SHIFT; count++; }
            if (count < 2) return;
            string why = refusal == null ? null : refusal(mods, (uint)key);
            if (why != null) { label.Text = why + " Faites une autre combinaison, ou Échap pour annuler."; return; }
            Mods = mods;
            Key = (uint)key;
            DialogResult = DialogResult.OK;
        }
    }

    // ---- Le questionnaire des consoles ----

    // Les prises de chaque écran qui change d'entrée sur ordre du PC : on nomme ce qui y est branché (« Xbox »), et chaque
    // nom reçoit un raccourci, qu'un clic change. « Voir » passe l'écran sur la prise 8 s, puis le ramène sur le PC.
    // L'entrée qu'un écran affiche à l'ouverture est celle du PC : le questionnaire ne s'ouvre pas pendant le jeu. Mêmes
    // couleurs que le panneau ; mesures en pixels à 96 ppp, mises à l'échelle par S().
    class ConsoleDialog : Form
    {
        const int W = 590, Edge = 10, Pad = 22, Inner = W - 2 * Pad, Seconds = 8;
        readonly Brightness light;
        readonly List<ConsoleInput> before;
        readonly List<ConsoleInput> kept = new List<ConsoleInput>(); // celles d'un écran absent ou muet : gardées telles quelles
        readonly List<uint> reserved = new List<uint>(); // les touches refusées avec Ctrl+Alt+Maj (le raccourci principal…)
        readonly Func<uint, uint, string> refusal;
        readonly CaptureKeys capture;
        readonly float scale;
        readonly List<Row> rows = new List<Row>();
        readonly List<Rectangle> cards = new List<Rectangle>();
        readonly Timer seeTimer = new Timer { Interval = 1000 };
        readonly ToolTip tips = new ToolTip();
        readonly Label reading = new Label { Text = "Lecture des écrans (quelques secondes)…", ForeColor = Ui.Dim, AutoSize = false };
        readonly RoundButton save = new RoundButton(string.Empty) { Text = "Enregistrer", Primary = true, Enabled = false };
        readonly RoundButton cancel = new RoundButton(string.Empty) { Text = "Annuler" };
        Color surface = Ui.Back;
        int y, cardTop, top, seeLeft;
        Row seeing;

        // Les consoles nommées, avec leur raccourci ; null si on a annulé.
        public List<ConsoleInput> Result { get; private set; }

        class Row
        {
            public ConsoleInput Entry; // la prise, son nom tapé et son raccourci
            public string Where;       // « HDMI 1, Écran 1 », pour le lecteur d'écran
            public TextBox Name;
            public RoundButton Key, See;
        }

        // refusal : pourquoi une combinaison ne peut pas servir (elle sert déjà dans Eclipse…), ou null ; capture : demander
        // une combinaison à la personne. Sans eux (images de la documentation), les raccourcis ne se changent pas.
        public ConsoleDialog(Brightness light, List<ConsoleInput> before, Func<uint, uint, string> refusal, CaptureKeys capture)
        {
            this.light = light;
            this.before = before;
            this.refusal = refusal;
            this.capture = capture;
            if (refusal != null) // les lettres automatiques évitent les combinaisons refusées
                for (uint k = '0'; k <= 'Z'; k++)
                    if ((k <= '9' || k >= 'A') && refusal(Hotkeys.Cas, k) != null) reserved.Add(k);
            Text = "Eclipse : consoles";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            DoubleBuffered = true;
            BackColor = Ui.Back;
            ForeColor = Ui.Text;
            Font = new Font(Ui.TextFont, 10f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;

            y = 14;
            Add(new Label { Text = Ui.Game, Font = new Font(Ui.IconFont, 11f), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false },
                Edge, y, 24, 24);
            Add(new Label { Text = "Consoles au clavier", Font = new Font(Ui.StrongFont, 10.5f), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft },
                Edge + 26, y, W - 2 * Edge - 26, 24);
            y += 32;
            Add(new Label { Text = "Nommez ce qui est branché sur chaque prise : une console, un autre PC… "
                + "Chaque nom reçoit un raccourci. Il passe l'écran sur la prise, puis le ramène sur le PC. "
                + "Cliquez sur un raccourci pour le changer.",
                ForeColor = Ui.Dim, AutoSize = false, UseMnemonic = false }, Edge, y, W - 2 * Edge, 64);
            y += 72;
            top = y;
            Add(reading, Pad, y, Inner, 24);
            y += 40;
            Add(cancel, W - Pad - 2 * 120 - 8, y, 120, 34);
            Add(save, W - Pad - 120, y, 120, 34);
            ClientSize = new Size(S(W), S(y + 34 + 16));

            seeTimer.Tick += delegate { SeeTick(); };
            save.Click += delegate { Save(); };
            cancel.Click += delegate { Close(); };
            KeyDown += OnKey;
            FormClosing += delegate { Back(); };
        }

        int S(int v) { return (int)Math.Round(v * scale); }

        void Add(Control c, int x, int at, int w, int h)
        {
            c.SetBounds(S(x), S(at), S(w), S(h));
            c.BackColor = surface;
            Controls.Add(c);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Ui.DarkTitle(Handle);
            Center();
            light.ReadInputs(read =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke(new Action(delegate { Fill(read); })); } catch (InvalidOperationException) { }
            });
        }

        void Center()
        {
            Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(work.Left + (work.Width - Width) / 2, work.Top + Math.Max(0, (work.Height - Height) / 2));
        }

        // Les écrans lus : une carte par écran, une ligne par prise.
        void Fill(List<ScreenLight> read)
        {
            if (IsDisposed) return;
            SuspendLayout();
            Controls.Remove(reading);
            reading.Dispose();
            y = top;
            List<string> ids = read.ConvertAll(s => s.Id);
            var shown = new List<int>(); // les écrans qui ont des lignes
            for (int i = 0; i < read.Count; i++)
            {
                ScreenLight s = read[i];
                string where = Ui.Place(i, read.Count, s.Bounds.X == 0 && s.Bounds.Y == 0);
                BeginCard();
                Add(new Label { Text = s.Name + (where.Length > 0 ? "  ·  " + where : string.Empty), Font = new Font(Ui.StrongFont, 10f),
                    AutoSize = false, AutoEllipsis = true, UseMnemonic = false }, Pad, y, Inner, 24);
                y += 30;
                int[] offered = Inputs.Offered(s.Inputs);
                string why = !s.Ok ? "Ne répond pas : activez DDC/CI dans son menu."
                    : offered == null ? "Ne change pas d'entrée sur ordre du PC."
                    : s.Input < 0 ? "Son entrée actuelle ne se lit pas : réessayez dans un moment."
                    : Array.TrueForAll(offered, code => code == s.Input) ? "Une seule entrée : rien à régler."
                    : null;
                if (why != null)
                {
                    Add(new Label { Text = why, ForeColor = Ui.Dim, AutoSize = false, UseMnemonic = false }, Pad, y, Inner, 22);
                    y += 22;
                    EndCard();
                    continue;
                }
                shown.Add(i);
                Add(new Label { Text = Inputs.NameOf(s.Input), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft }, Pad, y, 112, 30);
                Add(new Label { Text = "ce PC", ForeColor = Ui.Dim, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft }, Pad + 116, y, 150, 30);
                y += 34;
                foreach (int code in offered)
                {
                    if (code == s.Input) continue;
                    int index = i, input = code;
                    ConsoleInput old = before.Find(c => c.Input == input && Ddc.FindScreen(ids, c.Screen) == index);
                    AddRow(s, code, old);
                }
                y -= 4;
                EndCard();
            }
            foreach (ConsoleInput c in before)
                if (!shown.Contains(Ddc.FindScreen(ids, c.Screen))) kept.Add(c);
            y += 8;
            cancel.Top = S(y);
            save.Top = S(y);
            save.Enabled = rows.Count > 0;
            ClientSize = new Size(S(W), S(y + 34 + 16));
            ResumeLayout();
            ShowKeys();
            Center();
            Invalidate();
            if (rows.Count > 0) rows[0].Name.Focus();
        }

        // Une prise à nommer. Une console déjà réglée garde son nom, et son raccourci s'il a été choisi.
        void AddRow(ScreenLight s, int code, ConsoleInput old)
        {
            bool chosen = old != null && old.Chosen;
            string input = Inputs.NameOf(code);
            var row = new Row
            {
                Entry = new ConsoleInput { Name = old == null ? string.Empty : old.Name, Screen = s.Id, Input = code, Home = s.Input,
                    Chosen = chosen, Mods = chosen ? old.Mods : Hotkeys.Cas, Key = chosen ? old.Key : 0 },
                Where = input + ", " + s.Name
            };
            Add(new Label { Text = input, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft }, Pad, y, 112, 30);
            row.Name = new TextBox { Text = row.Entry.Name, MaxLength = 20, BorderStyle = BorderStyle.FixedSingle, ForeColor = Ui.Text,
                AccessibleName = "Ce qui est branché sur " + row.Where };
            Add(row.Name, Pad + 116, y + 3, 150, 24);
            row.Name.BackColor = Ui.Button;
            row.Name.TextChanged += delegate { ShowKeys(); };
            row.Key = new RoundButton(string.Empty) { AccessibleDescription = "Changer le raccourci", Visible = false };
            tips.SetToolTip(row.Key, row.Key.AccessibleDescription);
            row.Key.Click += delegate { ChangeKey(row); };
            Add(row.Key, Pad + 274, y + 1, 180, 28);
            row.See = new RoundButton(string.Empty) { Text = "Voir", AccessibleName = "Voir " + row.Where,
                AccessibleDescription = "Passe l'écran sur cette prise " + Seconds + " secondes, puis le ramène sur le PC." };
            tips.SetToolTip(row.See, row.See.AccessibleDescription);
            row.See.Click += delegate { See(row); };
            Add(row.See, W - Pad - 84, y + 1, 84, 28);
            rows.Add(row);
            y += 34;
        }

        void BeginCard()
        {
            cardTop = y;
            y += 12;
            surface = Ui.Card;
        }

        void EndCard()
        {
            y += 8;
            cards.Add(new Rectangle(S(Edge), S(cardTop), S(W - 2 * Edge), S(y - cardTop)));
            y += 12;
            surface = Ui.Back;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(Ui.Card))
                foreach (Rectangle c in cards)
                    using (GraphicsPath path = Ui.Round(c, S(8)))
                        e.Graphics.FillPath(brush, path);
        }

        // Les raccourcis, à mesure qu'on tape : dans l'ordre des lignes, comme à l'enregistrement. Une prise sans nom n'en a
        // pas : son bouton est caché.
        void ShowKeys()
        {
            Assign();
            foreach (Row r in rows)
            {
                r.Key.Text = r.Entry.Key == 0 ? "sans raccourci" : r.Entry.Shortcut;
                r.Key.AccessibleName = "Raccourci de " + r.Where + " : " + r.Key.Text;
                r.Key.Visible = r.Entry.Name.Length > 0;
            }
        }

        // Les noms tapés, puis les raccourcis automatiques (Hotkeys.Assign) ; ceux des consoles gardées sont pris.
        void Assign()
        {
            var taken = new List<uint>(reserved);
            foreach (ConsoleInput c in kept) if (c.Key != 0 && c.Mods == Hotkeys.Cas) taken.Add(c.Key);
            foreach (Row r in rows) r.Entry.Name = r.Name.Text.Trim();
            Hotkeys.Assign(rows.ConvertAll(r => r.Entry), taken);
        }

        // Un clic sur le raccourci d'une prise : la personne fait sa combinaison. Elle reste, même si le nom change.
        void ChangeKey(Row r)
        {
            uint mods, key;
            if (capture == null || !capture(this, "Raccourci de " + r.Entry.Name, (m, k) => Refusal(r, m, k), out mods, out key)) return;
            r.Entry.Mods = mods;
            r.Entry.Key = key;
            r.Entry.Chosen = true;
            ShowKeys();
        }

        // Pourquoi une combinaison ne peut pas aller à cette prise, ou null : elle sert déjà dans Eclipse, ou à une autre
        // console.
        string Refusal(Row r, uint mods, uint key)
        {
            string why = refusal == null ? null : refusal(mods, key);
            if (why != null) return why;
            Assign();
            Row other = rows.Find(x => x != r && x.Entry.Key == key && x.Entry.Mods == mods);
            ConsoleInput away = kept.Find(c => c.Key == key && c.Mods == mods);
            string name = other != null ? other.Entry.Name : away != null ? away.Name : null;
            return name == null ? null : Hotkeys.Text(mods, key) + " sert déjà à " + name + ".";
        }

        void Save()
        {
            Back();
            Assign();
            var list = new List<ConsoleInput>(kept);
            foreach (Row r in rows) if (r.Entry.Name.Length > 0) list.Add(r.Entry);
            Result = list;
            Close();
        }

        // « Voir » : l'écran passe sur la prise ; le bouton devient « Revenir » et compte les secondes.
        void See(Row r)
        {
            if (seeing != null) { bool same = seeing == r; Back(); if (same) return; }
            seeing = r;
            seeLeft = Seconds;
            foreach (Row x in rows) x.See.Enabled = x == r;
            r.See.Text = "Revenir  " + seeLeft;
            light.SetInput(r.Entry.Screen, r.Entry.Input, null);
            seeTimer.Start();
        }

        void SeeTick()
        {
            if (seeing == null) { seeTimer.Stop(); return; }
            if (--seeLeft <= 0) { Back(); return; }
            seeing.See.Text = "Revenir  " + seeLeft;
        }

        // Le retour sur le PC après « Voir » (aussi à la fermeture).
        void Back()
        {
            seeTimer.Stop();
            if (seeing == null) return;
            light.SetInput(seeing.Entry.Screen, seeing.Entry.Home, null);
            seeing.See.Text = "Voir";
            seeing = null;
            foreach (Row x in rows) x.See.Enabled = true;
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); }
            else if (e.KeyCode == Keys.Enter && ActiveControl is TextBox && save.Enabled) { e.Handled = e.SuppressKeyPress = true; Save(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { seeTimer.Dispose(); tips.Dispose(); }
            base.Dispose(disposing);
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

        const int HotkeyId = 1, LightUpId = 2, LightDownId = 3, FilterId = 4, ConsoleId = 100; // ConsoleId + n : la console n
        const string GameValue = "ConsoleEcran", SwappedValue = "ConsolePrincipal"; // l'état du jeu, pour un arrêt brutal
        const int LightStep = 10;
        const string RegKey = @"Software\Eclipse";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Secours = "Ctrl+Alt+Maj+Échap";
        static readonly TimeSpan RewakeGrace = TimeSpan.FromSeconds(5);
        static readonly TimeSpan SamePress = TimeSpan.FromSeconds(3);     // le même appui du raccourci, vu deux fois
        static readonly TimeSpan RelapseWatch = TimeSpan.FromSeconds(15); // écrans rééteints juste après le rallumage

        readonly NotifyIcon tray = new NotifyIcon();
        readonly ToolStripMenuItem offItem, screensItem, filterItem, consolesItem;
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
        // Les consoles. playing : celle à l'écran, ou null ; swapped : l'écran des consoles, s'il était l'écran principal
        // avant le jeu, à remettre ensuite. Les deux sont aussi dans le registre (GameValue, SwappedValue).
        List<ConsoleInput> consoles = ConsoleInput.Load();
        int consoleKeys;       // les raccourcis posés : ConsoleId à ConsoleId + consoleKeys - 1
        ConsoleInput playing;
        string swapped;
        bool switching, polling; // un changement d'entrée en cours ; une lecture de l'entrée en cours
        readonly Timer consolePoll = new Timer { Interval = 3000 };
        readonly Queue<Action> uiJobs = new Queue<Action>();
        ConsoleDialog consoleDialog;
        bool capturing;        // une fenêtre de capture est ouverte : les raccourcis d'Eclipse sont rendus à Windows

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
            filterItem = new ToolStripMenuItem("Lumière bleue");
            filterItem.DropDownItems.Add(new ToolStripMenuItem("Pas de filtre", null, delegate { SetFilter(BlueFilter.Non); }));
            filterItem.DropDownItems.Add(new ToolStripMenuItem("Filtrée sous le minimum", null, delegate { SetFilter(BlueFilter.SousLeMin); }));
            filterItem.DropDownItems.Add(new ToolStripMenuItem("Toujours filtrée", null, delegate { SetFilter(BlueFilter.Toujours); })
                { ShortcutKeyDisplayString = "Ctrl+Alt+Maj+B" });
            filterItem.DropDownOpening += delegate
            {
                for (int i = 0; i < filterItem.DropDownItems.Count; i++)
                    ((ToolStripMenuItem)filterItem.DropDownItems[i]).Checked = i == (int)dimmer.Mode;
            };
            menu.Items.Add(filterItem);
            consolesItem = new ToolStripMenuItem("Consoles");
            menu.Items.Add(consolesItem);
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
            menu.Opening += delegate { RefreshLabels(); FillConsolesMenu(); };
            FillConsolesMenu(); // la flèche du sous-menu dès le premier clic droit

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
            bool mainKey = RegisterKeys(true);
            consolePoll.Tick += delegate { PollConsole(); };
            Try("les consoles", ResumeGame);
            if (!mainKey)
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
            panel = new BrightnessPanel(light, dimmer, "Éteindre les écrans (" + ShortcutText() + ")", Begin, consoles, playing, PickConsole);
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

        // ---- La lumière bleue ----

        // Ctrl+Alt+Maj+B : « Toujours filtrée », ou le choix habituel, avec le rappel en bas de l'écran.
        void ToggleFilter()
        {
            dimmer.Toggle();
            if (panel != null && !panel.IsDisposed) panel.ShowFilter();
            if (osd == null || osd.IsDisposed) osd = new LightOsd();
            osd.FlashFilter(dimmer.Mode, dimmer.Chaleur);
        }

        void SetFilter(BlueFilter mode)
        {
            dimmer.SetMode(mode);
            if (panel != null && !panel.IsDisposed) panel.ShowFilter();
        }

        void OnDisplayChanged(object sender, EventArgs e)
        {
            light.Refresh(null); // un écran branché, débranché ou déplacé : on relit tout
            // L'écran des consoles revenu après le jeu (il était débranché) : il redevient l'écran principal.
            if (playing == null && !switching && swapped != null) Try("l'écran principal", RestorePrimary);
        }

        // ---- Les consoles ----

        // Les travaux rendus par le fil des écrans, faits ici, au fil principal (WM_APP_RUN).
        void OnUi(Action job)
        {
            lock (uiJobs) uiJobs.Enqueue(job);
            Native.PostMessage(Handle, Native.WM_APP_RUN, IntPtr.Zero, IntPtr.Zero);
        }

        void RunUiJobs()
        {
            for (;;)
            {
                Action job;
                lock (uiJobs)
                {
                    if (uiJobs.Count == 0) return;
                    job = uiJobs.Dequeue();
                }
                Try("les consoles", job);
            }
        }

        // Les raccourcis d'Eclipse, le principal d'abord : éteindre passe avant les autres (la 1.0 acceptait par exemple
        // Ctrl+Alt+Maj+B). tell : dire ceux qu'une autre appli a déjà pris (au démarrage).
        bool RegisterKeys(bool tell)
        {
            bool mainKey = RegisterCurrentHotkey();
            if ((!Native.RegisterHotKey(Handle, LightUpId, Hotkeys.Cas, Native.VK_UP)
                | !Native.RegisterHotKey(Handle, LightDownId, Hotkeys.Cas, Native.VK_DOWN)) && tell)
                Journal.Write("Luminosité : Ctrl+Alt+Maj+Haut ou Bas est déjà pris.");
            if (!Native.RegisterHotKey(Handle, FilterId, Hotkeys.Cas | Native.MOD_NOREPEAT, (uint)Keys.B) && tell)
                Journal.Write("Lumière bleue : Ctrl+Alt+Maj+B est déjà pris.");
            RegisterConsoleKeys(tell);
            return mainKey;
        }

        // Un raccourci déjà pris ailleurs n'est plus annoncé (menu, message, panneau) ; tell : une bulle le dit.
        void RegisterConsoleKeys(bool tell)
        {
            for (int i = 0; i < consoleKeys; i++) Native.UnregisterHotKey(Handle, ConsoleId + i);
            consoleKeys = consoles.Count;
            var refused = new List<string>();
            for (int i = 0; i < consoles.Count; i++)
            {
                ConsoleInput c = consoles[i];
                c.Refused = c.Key != 0 && !Native.RegisterHotKey(Handle, ConsoleId + i, c.Mods | Native.MOD_NOREPEAT, c.Key);
                if (c.Refused) refused.Add(c.Shortcut);
            }
            if (refused.Count == 0 || !tell) return;
            string keys = string.Join(", ", refused);
            Journal.Write("Consoles : " + keys + " déjà pris par une autre appli.");
            tray.ShowBalloonTip(5000, "Eclipse", keys + (refused.Count > 1 ? " sont déjà pris" : " est déjà pris")
                + " par une autre appli. Pour en choisir un autre : menu de l'icône, Consoles, Régler les consoles.", ToolTipIcon.Warning);
        }

        // Une combinaison demandée à la personne. Pendant ce temps, les raccourcis d'Eclipse sont rendus à Windows : la
        // fenêtre reçoit la combinaison, au lieu qu'une console bascule ou que la lumière bleue change. Une seule à la fois.
        bool Capture(IWin32Window owner, string title, Func<uint, uint, string> refusal, out uint mods, out uint key)
        {
            mods = key = 0;
            if (capturing) return false;
            capturing = true;
            foreach (int id in new[] { HotkeyId, LightUpId, LightDownId, FilterId }) Native.UnregisterHotKey(Handle, id);
            for (int i = 0; i < consoleKeys; i++) Native.UnregisterHotKey(Handle, ConsoleId + i);
            try
            {
                using (var dialog = new HotkeyDialog(title, refusal))
                {
                    if (owner != null) dialog.StartPosition = FormStartPosition.CenterParent;
                    if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
                    mods = dialog.Mods;
                    key = dialog.Key;
                    return true;
                }
            }
            finally
            {
                capturing = false;
                RegisterKeys(false);
            }
        }

        // Pourquoi une combinaison ne peut pas aller à une console, ou null.
        string ConsoleRefusal(uint mods, uint key) { return Hotkeys.ForConsole(mods, key, hotMods, hotKey, Native.GetKeyboardLayout(0)); }

        // Pourquoi une combinaison ne peut pas devenir le raccourci principal, ou null.
        string MainRefusal(uint mods, uint key) { return Hotkeys.ForMain(mods, key, consoles, Native.GetKeyboardLayout(0)); }

        void FillConsolesMenu()
        {
            var old = new List<ToolStripItem>();
            foreach (ToolStripItem item in consolesItem.DropDownItems) old.Add(item);
            consolesItem.DropDownItems.Clear();
            foreach (ToolStripItem item in old) item.Dispose();
            foreach (ConsoleInput c in consoles)
            {
                ConsoleInput target = c;
                consolesItem.DropDownItems.Add(new ToolStripMenuItem(c.Name.Replace("&", "&&"), null, delegate { ToggleConsole(target); })
                    { Checked = c == playing, ShortcutKeyDisplayString = c.WorkingShortcut });
            }
            if (playing != null)
                consolesItem.DropDownItems.Add(new ToolStripMenuItem("Revenir sur le PC", null, delegate { if (playing != null) ToggleConsole(playing); })
                    { ShortcutKeyDisplayString = playing.WorkingShortcut });
            if (consoles.Count > 0) consolesItem.DropDownItems.Add(new ToolStripSeparator());
            consolesItem.DropDownItems.Add(new ToolStripMenuItem(playing == null ? "Régler les consoles..." : "Régler les consoles (depuis le PC)",
                null, delegate { SetUpConsoles(); }) { Enabled = playing == null });
        }

        static bool SameScreen(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        // Le raccourci d'une console : l'écran passe dessus ; le même raccourci le ramène sur le PC. Celui d'une autre console
        // du même écran passe de l'une à l'autre.
        void ToggleConsole(ConsoleInput c)
        {
            if (state != State.Idle || switching) return;
            if (playing == null) GoConsole(c);
            else if (playing == c) GoHome(null);
            else if (SameScreen(playing.Screen, c.Screen)) GoConsole(c);
            else GoHome(delegate { GoConsole(c); }); // une console d'un autre écran : le premier revient d'abord sur le PC
        }

        // Depuis le panneau ; c vaut null pour « PC ».
        void PickConsole(string screen, ConsoleInput c)
        {
            if (c != null) { if (c != playing) ToggleConsole(c); }
            else if (playing != null && SameScreen(playing.Screen, screen)) ToggleConsole(playing);
        }

        // Vers la console. Si son écran est l'écran principal, son voisin le devient d'abord, pendant que l'écran montre
        // encore le PC ; puis l'écran change d'entrée. Un message dit, sur l'écran du PC, comment revenir.
        void GoConsole(ConsoleInput c)
        {
            string device = PrimaryScreen.DeviceOf(c.Screen);
            if (device == null) { Notice(c.Name + " : son écran n'est pas branché", null); return; }
            if (playing == null && swapped == null && PrimaryScreen.IsPrimary(device))
            {
                string neighbour = PrimaryScreen.Neighbour(device);
                if (neighbour != null && PrimaryScreen.Set(neighbour))
                {
                    swapped = c.Screen;
                    SaveGameValue(SwappedValue, swapped);
                }
            }
            switching = true;
            light.SetInput(c.Screen, c.Input, ok => OnUi(delegate
            {
                switching = false;
                if (!ok)
                {
                    if (playing == null) RestorePrimary();
                    Notice(c.Name + " : l'écran ne répond pas", device);
                    return;
                }
                playing = c;
                SaveGameValue(GameValue, c.Screen);
                consolePoll.Start();
                Journal.Write("Consoles : écran passé sur " + c.Name + " (" + Inputs.NameOf(c.Input) + ").");
                Notice(c.Name + "  ·  retour au PC : " + (c.WorkingShortcut ?? "menu de l'icône, Consoles"), device);
            }));
        }

        // Vers le PC : l'écran reprend l'entrée du PC, puis redevient l'écran principal s'il l'était. then : la suite, si
        // le retour a marché.
        void GoHome(Action then)
        {
            ConsoleInput c = playing;
            if (c == null) return;
            string device = PrimaryScreen.DeviceOf(c.Screen);
            if (device == null)
            {
                EndGame("l'écran n'est plus vu par Windows");
                Notice("L'écran de " + c.Name + " n'est plus vu par Windows : utilisez son bouton des entrées", null);
                return;
            }
            switching = true;
            light.SetInput(c.Screen, c.Home, ok => OnUi(delegate
            {
                switching = false;
                if (playing != c) return;
                if (!ok) { Notice("L'écran ne répond pas : réessayez, ou utilisez son bouton des entrées", device); return; }
                EndGame("retour au PC");
                if (then != null) then();
            }));
        }

        // La fin du jeu, l'écran déjà revenu sur le PC (ou plus vu par Windows).
        void EndGame(string why)
        {
            playing = null;
            consolePoll.Stop();
            SaveGameValue(GameValue, null);
            RestorePrimary();
            Journal.Write("Consoles : fin du jeu (" + why + ").");
        }

        // L'écran des consoles redevient l'écran principal s'il l'était avant le jeu ; débranché, il le redevient à son
        // retour (OnDisplayChanged) ou au prochain démarrage.
        void RestorePrimary()
        {
            if (swapped == null) return;
            string device = PrimaryScreen.DeviceOf(swapped);
            if (device == null || !PrimaryScreen.Set(device)) return;
            swapped = null;
            SaveGameValue(SwappedValue, null);
        }

        // Pendant le jeu, toutes les 3 s : l'entrée de l'écran (règle de ConsoleInput.Read). Revenu seul sur le PC (sans
        // image, l'écran cherche une autre entrée) ou par son bouton : le jeu s'arrête. Passé sur une autre console réglée :
        // elle devient celle à l'écran. Pas de réponse : on attend la lecture suivante.
        void PollConsole()
        {
            ConsoleInput c = playing;
            if (c == null) { consolePoll.Stop(); return; }
            if (polling || switching || state != State.Idle) return;
            polling = true;
            light.GetInput(c.Screen, code => OnUi(delegate
            {
                polling = false;
                if (playing != c || switching) return;
                ConsoleInput shown;
                Seen seen = ConsoleInput.Read(code, c.Screen, consoles, out shown);
                if (seen == Seen.Pc) EndGame("l'écran est revenu sur le PC");
                else if (seen == Seen.Console && shown != c)
                {
                    playing = shown;
                    Journal.Write("Consoles : l'écran est passé sur " + shown.Name + ".");
                }
            }));
        }

        // Au démarrage, après un arrêt d'Eclipse pendant le jeu (arrêt brutal, mise à jour, arrêt de Windows), la même règle
        // que la lecture des 3 s : l'écran montre le PC, tout revient ; il montre une console, ou ne répond pas encore, le
        // jeu continue et la lecture des 3 s tranchera.
        void ResumeGame()
        {
            swapped = LoadGameValue(SwappedValue);
            string screen = LoadGameValue(GameValue);
            if (screen == null) { RestorePrimary(); return; }
            light.GetInput(screen, code => OnUi(delegate
            {
                if (playing != null || switching) return;
                ConsoleInput c;
                if (ConsoleInput.Read(code, screen, consoles, out c) == Seen.Pc) { EndGame("Eclipse arrêté pendant le jeu"); return; }
                if (c == null) c = consoles.Find(x => SameScreen(x.Screen, screen)); // en attendant que l'écran réponde
                playing = c;
                consolePoll.Start();
                Journal.Write("Consoles : le jeu continue sur " + c.Name + (code < 0 ? " (l'écran ne répond pas encore)." : "."));
            }));
        }

        // Eclipse s'arrête pendant le jeu. Quitter ramène l'écran sur le PC : sans Eclipse, plus de raccourci pour revenir.
        // L'arrêt de Windows le laisse sur la console. Le jeu ne se termine (l'écran principal rendu) que si l'écran est
        // bien revenu sur le PC, ou à l'arrêt de Windows quand Eclipse ne redémarre pas avec lui. Sinon le registre garde le
        // jeu et le prochain démarrage décide (ResumeGame) : l'écran principal reste celui qu'on voit, même pour se connecter.
        void LeaveGame(bool backToPc)
        {
            ConsoleInput c = playing;
            if (c == null) { RestorePrimary(); return; }
            if (backToPc)
            {
                bool ok = false, back;
                using (var done = new ManualResetEvent(false))
                {
                    light.SetInput(c.Screen, c.Home, result => { ok = result; try { done.Set(); } catch (ObjectDisposedException) { } });
                    back = done.WaitOne(6000) && ok;
                }
                if (back) EndGame("Quitter");
                else SuspendGame("Quitter, l'écran ne revient pas sur le PC");
            }
            else if (StartupEnabled()) SuspendGame("arrêt de Windows pendant le jeu");
            else EndGame("arrêt de Windows");
        }

        // Le jeu laissé au prochain démarrage : oublié ici, gardé dans le registre (ResumeGame le reprend).
        void SuspendGame(string why)
        {
            playing = null;
            swapped = null; // ni Quitter ni un changement d'affichage ne doivent plus le rendre ici
            consolePoll.Stop();
            Journal.Write("Consoles : " + why + " ; le prochain démarrage reprendra le jeu.");
        }

        void Notice(string text, string notOn)
        {
            if (osd == null || osd.IsDisposed) osd = new LightOsd();
            osd.FlashMessage(text, notOn);
        }

        // Le questionnaire, seulement depuis le PC : l'entrée que chaque écran affiche à l'ouverture est celle du PC.
        void SetUpConsoles()
        {
            if (state != State.Idle || playing != null || switching || capturing) return;
            if (consoleDialog != null && !consoleDialog.IsDisposed) { consoleDialog.Activate(); return; }
            var dialog = new ConsoleDialog(light, consoles, ConsoleRefusal, Capture);
            consoleDialog = dialog;
            dialog.FormClosed += delegate
            {
                consoleDialog = null;
                if (dialog.Result == null) return;
                consoles = dialog.Result;
                ConsoleInput.Save(consoles);
                RegisterConsoleKeys(true);
                if (playing != null) // une console prise au clavier pendant le questionnaire
                    playing = consoles.Find(x => SameScreen(x.Screen, playing.Screen) && x.Input == playing.Input) ?? playing;
                Journal.Write("Consoles : " + consoles.Count + " réglée(s).");
            };
            dialog.Show();
            dialog.Activate();
        }

        static string LoadGameValue(string name)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    string v = key == null ? null : key.GetValue(name) as string;
                    return string.IsNullOrEmpty(v) ? null : v;
                }
            }
            catch { return null; }
        }

        static void SaveGameValue(string name, string value)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKey))
                {
                    if (value == null) key.DeleteValue(name, false);
                    else key.SetValue(name, value);
                }
            }
            catch (Exception e) { Journal.Write("Consoles : état du jeu non enregistré (" + e.Message + ")."); }
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
                    if (m.WParam.ToInt32() == FilterId)
                    {
                        if (state == State.Idle) ToggleFilter();
                        return;
                    }
                    int console = m.WParam.ToInt32() - ConsoleId;
                    if (console >= 0 && console < consoleKeys)
                    {
                        if (state == State.Idle && console < consoles.Count) ToggleConsole(consoles[console]);
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
                case Native.WM_APP_RUN:
                    RunUiJobs();
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
                    if (m.WParam != IntPtr.Zero)
                    {
                        sound.RunAndWait(delegate { Sound.Restore(); }, 3000);
                        Try("les consoles", delegate { LeaveGame(false); });
                    }
                    break;
            }
            base.WndProc(ref m);
        }

        string ShortcutText() { return Hotkeys.Text(hotMods, hotKey); }

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
                    if (mods is int && vk is int) { hotMods = Hotkeys.ModsFrom(mods); hotKey = (uint)(int)vk; }
                }
            }
            catch { }
        }

        void ChangeHotkey()
        {
            // Le questionnaire ouvert règle des raccourcis pas encore enregistrés : on le finit d'abord.
            if (consoleDialog != null && !consoleDialog.IsDisposed) { consoleDialog.Activate(); return; }
            uint mods, key;
            if (state != State.Idle || !Capture(null, "Changer le raccourci", MainRefusal, out mods, out key)) return;
            if (mods == hotMods && key == hotKey) return;
            uint oldMods = hotMods, oldKey = hotKey;
            Native.UnregisterHotKey(Handle, HotkeyId);
            hotMods = mods;
            hotKey = key;
            if (!RegisterCurrentHotkey())
            {
                MessageBox.Show(ShortcutText() + " est déjà pris par une autre appli. On garde l'ancien.", "Eclipse");
                hotMods = oldMods;
                hotKey = oldKey;
                RegisterCurrentHotkey();
            }
            else
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegKey))
                {
                    k.SetValue("Mods", (int)hotMods);
                    k.SetValue("Key", (int)hotKey);
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
                + "\n\nLuminosité des écrans : un clic sur l'icône, ou Ctrl+Alt+Maj+Haut / Bas (10 % à la fois). Sous le seuil « min », l'image est assombrie au-delà du minimum de l'écran. Un double-clic sur l'icône éteint."
                + "\n\nLumière bleue : Ctrl+Alt+Maj+B la filtre tout le temps, ou plus. Dans le panneau, choisissez aussi de la filtrer seulement sous le minimum, et sa chaleur."
                + "\n\nConsoles : nommez les prises de vos écrans (menu de l'icône, Consoles). Chaque nom reçoit un raccourci, Ctrl+Alt+Maj et une lettre, qui passe l'écran sur la console ; le même raccourci le ramène sur le PC. Pour en choisir un autre, cliquez dessus dans le questionnaire.",
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
            if (consoleDialog != null && !consoleDialog.IsDisposed) consoleDialog.Close(); // après « Voir » : l'écran revient
            Try("les consoles", delegate { LeaveGame(true); });
            consolePoll.Dispose();
            Journal.Write("Quitté.");
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.UserPreferenceChanged -= OnThemeChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            Native.UnregisterHotKey(Handle, HotkeyId);
            Native.UnregisterHotKey(Handle, LightUpId);
            Native.UnregisterHotKey(Handle, LightDownId);
            Native.UnregisterHotKey(Handle, FilterId);
            for (int i = 0; i < consoleKeys; i++) Native.UnregisterHotKey(Handle, ConsoleId + i);
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
