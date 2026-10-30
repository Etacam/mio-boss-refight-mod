using MioGame;
using MioModdingApi;
using MioModLoader;
using System.Runtime.InteropServices;
using static MioGame.GlobalFunctions.engine.gui;
using static MioGame.GlobalFunctions.Functions;
using System.Text;

namespace MioBossRefightMod;

public unsafe class MioBossRefightMod : Mod
{

    // ---- ImGui enum values ----
    private const int Cond_FirstUseEver = 1 << 2;    // 4
    private const int Cond_Always = 1 << 0;    // 1
    private const int WindowFlags_NoDocking = 1 << 21;

    private const int VK_INSERT = 0x2D;
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    private readonly Dictionary<string, BossInfo> Boss_Locations = new Dictionary<string, BossInfo>
    {
        {"Ati", new BossInfo(-1441f, 1703f, "BOSS:SASUKE")},
        {"Egis", new BossInfo(-1680f, 1539f, "BOSS:CRABASH")},
        {"Acat", new BossInfo(-2046f, 1868f, "BOSS:BLACKSMITH")},
        {"Atmos", new BossInfo(-2650f, 1413f, "BOSS:TRAPU")},
        {"Poltergates", new BossInfo(-2582f, 1576f, "BOSS:DOOR")},
        {"Calderon", new BossInfo(-3199f, 1752f, "BOSS:ZANGY")},
        {"Ancille & Targa", new BossInfo(-3629f, 1846f, "BOSS:TWIN_CRABASH")},
        {"Sol & Vin", new BossInfo(-2541f, 2309f, "BOSS:TWIN")},
        {"Flora", new BossInfo(-815f, 1426f, "BOSS:VINE")},
        {"Crow", new BossInfo(-1229f, 1638f, "BOSS:SCARECROW")},
        {"Shii", new BossInfo(-123f, 2420f, "BOSS:SHII")},
        {"Friends :)", new BossInfo(48, 1915f, "BOSS:SPIDY")},
        {"Nabuu", new BossInfo(-272f, 1901f, "BOSS:BAMBY")},
        {"Lombre", new BossInfo(-251f, 1634f, "BOSS:BAMBY")},
        {"AnRa", new BossInfo(-3215f, 482f, "BOSS:PROJEKT")}, // TODO: this will need extra stuff to make work
        {"Debby", new BossInfo(-1647f, 440f, "BOSS:RAMBO")}, // TODO: this will need extra stuff to make work
        {"Sawlong", new BossInfo(-1397f, 593f, "BOSS:SAW_LONGUE")},
        {"Vlad", new BossInfo(-916f, 464f, "BOSS:WASHING_MACHINE")},
    };

    // ---- gui state ----
    private bool _isOpen;          // menu starts closed; INSERT opens it
    private bool _insertWasDown;
    private bool _pollPrimed;
    private string Boss_refight_request = "";
    private int Boss_refight_step = -1;
    private bool Nexus_teleport = false;

    // boss health state
    private string boss_name = "";
    private float boss_cur_health = 0f;
    private float boss_max_health = 0f;
    private float boss_init_max_health = 0f;
    private float boss_decay_health = 0f;
    private float last_hit_amount = 0f;

    public override void Initialize()
    {
        On.MioGame.GlobalFunctions.engine.On_gui.gui_render.Prefix += Gui_render_Prefix;
        On.MioGame.On_Save_thread.platform_save_game.Hook += Platform_save_hook;
        On.MioGame.GlobalFunctions.game.On_boss.default_boss_health_bar.Prefix += Boss_health_bar_prefix;
        On.MioGame.On_Game.fixed_update.Prefix += Fixed_update_Prefix;
    }

    private void Gui_render_Prefix()
    {
        try
        {
            PollToggle();
            UpdateCursor();

            if (!_isOpen) return;

            // The devs added a custom kill switch to their ImGui fork. While
            // gui_disabled is set, Begin() marks every window Hidden and
            // returns false. Clear it for our windows, then restore it so the
            // game's own F11 logic stays in sync.
            bool wasDisabled = ImGui__is_gui_disabled();
            if (wasDisabled) ImGui__enable_gui();

            try
            {
                if (_isOpen) DrawMenu();
            }
            finally
            {
                if (wasDisabled) ImGui__disable_gui();
            }
        }
        catch (Exception ex)
        {
            // Runs on the game's render thread; never let this escape.
            LogMessage("[gui] draw error: " + ex);
        }
    }

    private void Boss_health_bar_prefix(TNode_Boss_fight_node bf, Boss_analyzer_state* s)
    {
        Val_ref boss_data = bf.d->boss_node->data;
        MioGame.Boss* boss_info = (Boss*)boss_data.mem;

        boss_name = Util.MioStringToString(*boss_data.struct_name());

        float delta = Math.Abs(boss_cur_health - boss_info->health.rt.cur_health);
        if (delta > 0.1)
        {
            last_hit_amount = delta;
        }

        boss_max_health = boss_info->health.max_health;
        boss_init_max_health = boss_info->health.initial_max_health;
        boss_cur_health = boss_info->health.rt.cur_health;
        boss_decay_health = boss_info->health.decayed_health;
    }

    private bool Platform_save_hook(On.MioGame.On_Save_thread.orig_platform_save_game orig, Save_thread* self, uint slot, Array_unsigned_char* data)
    {
        LogMessage($"Save requested for slot {slot}");
        return false;
    }

    private void Fixed_update_Prefix(MioGame.Game* __this)
    {
        Handle_boss_refight(*__this);
        if (Nexus_teleport)
        {
            __this->mio.move_to(new Vec_float_3() { Base = new _vec_storage_float_3() { x = -1389f, y = 1551f, z = 0.0f } });
            Nexus_teleport = false;
        }
    }

    private void PollToggle()
    {
        bool down = (GetAsyncKeyState(VK_INSERT) & 0x8000) != 0;

        // First frame: record the key state without acting on it, so a key
        // already held at startup doesn't register as a fresh press.
        if (!_pollPrimed)
        {
            _pollPrimed = true;
            _insertWasDown = down;
            return;
        }

        if (down && !_insertWasDown) _isOpen = !_isOpen;
        _insertWasDown = down;
    }

    /// <summary>
    /// The game re-asserts cursor visibility every frame, so this must be
    /// called every frame rather than only on change.
    /// </summary>
    private void UpdateCursor()
    {
        try { toggle_mouse_cursor(_isOpen); }
        catch (Exception ex) { LogMessage("[gui] cursor toggle failed: " + ex); }
    }

    private void DrawMenu()
    {
        var pos = new ImVec2 { x = 12f, y = 12f };
        var pivot = new ImVec2 { x = 0f, y = 0f };
        var size = new ImVec2 { x = 260f, y = 600f };

        ImGui__SetNextWindowPos(&pos, Cond_FirstUseEver, &pivot);
        ImGui__SetNextWindowSize(&size, Cond_FirstUseEver);
        ImGui__SetNextWindowCollapsed(false, Cond_Always);
        ImGui__SetNextWindowBgAlpha(0.92f);

        fixed (byte* title = Utf8("Boss"))
        {
            bool open = _isOpen;

            if (ImGui__Begin((sbyte*)title, &open, WindowFlags_NoDocking))
            {

                ImGui__Separator();

                Text($"Boss health: {boss_name}");
                Text($"Current/max: {boss_cur_health:f2}/{boss_max_health:f2}");
                Text($"Decayed: {boss_decay_health:f2}");
                Text($"Init max: {boss_init_max_health:f2}");
                Text($"Last hit: {last_hit_amount:f2}");

                ImGui__Separator();

                foreach (string boss in Boss_Locations.Keys)
                {
                    if (!AddBossButton(boss)) open = false;
                }

                ImGui__Separator();

                if (Button("Return to Nexus", 120f)) Nexus_teleport = true;

                if (Button("Close", 120f)) open = false;
                Text("INSERT = toggle menu");
            }
            ImGui__End();

            _isOpen = open;
        }
    }

    private bool AddBossButton(string name)
    {
        if (Button(name, 120f))
        {
            Boss_refight_step = 0;
            Boss_refight_request = name;
            return false;
        }
        return true;
    }

    private void Handle_boss_refight(MioGame.Game game)
    {
        Mio mio = game.mio;
        if (Boss_refight_step != -1 && Boss_refight_request != "")
        {
            BossInfo? boss = Boss_Locations.GetValueOrDefault(Boss_refight_request);
            if (boss != null)
            {
                if (Boss_refight_step == 0)
                {
                    mio.move_to(new Vec_float_3());
                    Boss_refight_step++;
                }
                else if (Boss_refight_step == 1)
                {
                    RemoveFlag(game, boss.flag);
                    if (Boss_refight_request == "Debby")
                    {
                        game.save.plotpoints.mel.quest = Plotpoints.Mel.Quest.Captured;
                        game.save.plotpoints.mel.rambo_awoken = false;
                    }
                    if (Boss_refight_request == "AnRa")
                    {
                        RemoveFlag(game, "VOICE:HALYN");
                    }
                    if (Boss_refight_request == "Ati" || Boss_refight_request == "Shii")
                    {
                        var s = Util.StringToMioString("VOICE:HALYN");
                        game.save.acquire(&s);
                    }
                    Boss_refight_step++;
                }
                else if (Boss_refight_step == 2)
                {
                    mio.move_to(new Vec_float_3() { Base = new _vec_storage_float_3() { x = boss.x, y = boss.y, z = 0.0f } });
                    mio.life.health_points = mio.life.max_health_points;

                    Boss_refight_step = -1;
                    Boss_refight_request = "";
                }
            }
            else
            {
                LogMessage($"Failed to find boss '{Boss_refight_request}'");
                Boss_refight_step = -1;
                Boss_refight_request = "";
            }
        }
    }

    private static void RemoveFlag(MioGame.Game game, string flag)
    {
        MioGame.String s = Util.StringToMioString(flag);
        Save_entry* entry = game.save.peek(&s);

        game.save.entries.try_remove(&s, entry);
    }

    private static byte[] Utf8(string s)
    {
        byte[] b = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, 0, s.Length, b, 0);
        return b;   // trailing 0 from array init
    }

    private static bool Button(string label, float width)
    {
        var size = new ImVec2 { x = width, y = 0f };
        fixed (byte* l = Utf8(label))
            return ImGui__Button((sbyte*)l, &size);
    }

    private static void Text(string s)
    {
        fixed (byte* t = Utf8(s))
            ImGui__TextUnformatted((sbyte*)t, null);
    }

}

internal record BossInfo(float x, float y, string flag);