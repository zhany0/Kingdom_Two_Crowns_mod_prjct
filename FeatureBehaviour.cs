using System;
using System.Collections.Generic;
using Coatsink.Common;
using HarmonyLib;
using UnityEngine;

namespace BetterKingdom
{
    /// <summary>
    /// 功能实现：钱袋/国库联动、墙体内部守墙修正、摇铃立即上船。
    /// 设置窗口只负责开关状态，本文件负责这些功能的运行时逻辑与 Harmony 补丁。
    /// </summary>
    public class FeatureBehaviour : MonoBehaviour
    {
        // 注：构建链为 .NET SDK 6（最高 C# 10），请勿使用主构造函数/集合表达式等 C# 12 语法
        public FeatureBehaviour(IntPtr ptr) : base(ptr) { }

        // ---- 周期刷新总体节奏 ----
        // 设计原则：安全相关（怪物检测）保持较高频率；纯显示/纠正类任务降频；
        // 所有重型任务共用 TryReserveHeavySlot 的本帧唯一槽位，且相邻两个重型任务之间至少空一帧，
        // 把全场景扫描/大集合遍历摊平到多帧，避免同一帧叠多个重活造成偶发卡顿。
        private const float CacheInterval = 0.5f;            // 玩家/城堡/税官缓存刷新间隔（数值变化慢，2Hz 足够）
        private const float HeavyInterval = 3f;              // 墙体/守卫槽修正间隔（纠错类，不需要 2 秒一次）
        private const float WallBoundsScanInterval = 5f;     // 墙体边界重建间隔（仅供弓箭手守墙纠错使用）
        private const float SafetyBoundsScanInterval = 0.5f; // 避怪距离参照的城墙边界刷新间隔（建墙后尽快生效）
        private const float FleeEnemyScanInterval = 0.3f;    // 避怪强制判定的敌人扫描间隔（ShouldFlee 热路径只查这份缓存）
        private const float WallQueueClampInterval = 1f;     // 禁用排队时复钳间隔（分配补丁已即时复钳，这里只做兜底）
        private const float WorkBalanceInterval = 1f;        // 排队工作就近改派检测间隔（改派晚 1 秒无感知）
        private const float WorkBalanceMargin = 0.5f;       // 就近改派的最小距离优势，避免频繁来回改派
        private const int PurseMaxCoins = 38;        // 钱袋容量上限（钻石占 3 金币位，有效金币上限 = 38 - 3×钻石数）
        private const float PayHoldThreshold = 0.35f; // 长按判定阈值（秒），短点击不触发取款
        private const float BankerScanInterval = 3f;    // 银行家（税官）场景查找最小间隔（全场景遍历，两个组件共用一份缓存）
        private const float StationSideCheckInterval = 2f;  // 传送门被毁后的驻守侧改派检测间隔（只在单侧无门时才有写操作）
        private const int HeavySlotGapFrames = 1;           // 两个重型任务之间至少空出的帧数
        private const float InitialFleeRange = 6f;      // 避怪距离兜底值：与游戏原生 Worker/Archer.fleeRange 一致（连主城都取不到时才用）
        private const float FleeRangeWallFactor = 1.2f; // 避怪距离 = 自身距该侧城墙外沿的距离 × 此系数；已在墙内则置 0
        private const float PlayerRunSpeedBoostRatio = 1.5f; // 玩家奔跑速度提升比例：按当前坐骑基础奔跑速度 ×1.5（+50%）

        private Player _cachedPlayer;
        private Player _cachedPlayer2;
        private Castle _cachedCastle;
        private Banker _cachedBanker;
        // 银行家（税官）全局缓存：本组件与地图覆盖层共用一份，场景查找按 BankerScanInterval 限频
        private static Banker _sharedBanker;
        private static int _sharedBankerKingdomId = -1;
        private static float _nextBankerScan;
        private static bool _isMenuOpen;
        private float _payHoldStartP1 = -1f, _payHoldStartP2 = -1f;
        private float _nextCacheRefresh, _nextHeavyWork, _nextWallQueueClamp;
        private static float _nextWallBoundsScan;              // 墙体边界下一次重建时刻（弓箭手纠错）
        private static float _nextSafetyBoundsScan;            // 城墙边界/避怪距离的下一次刷新时刻
        private static float _nextFleeEnemyScan;               // 下一次危险敌人扫描时刻（供 ShouldFlee 强制补丁使用）
        private static readonly List<float> _fleeEnemyX = new(); // 危险敌人世界X缓存（仅X轴，与原生避怪比较口径一致）
        internal static readonly HashSet<int> _stickyFleeWorkers = new(); // 已触发避怪的工人实例ID：该侧进攻怪物清零前持续避怪、不出外工作
        internal static readonly HashSet<int> _stickyFleeArchers = new(); // 已触发避怪的弓箭手实例ID：逻辑与工人避怪粘滞相同
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Enemy> _fleeEnemyBuffer;
        private static int _cachedKingdomId = -1;             // 用于检测换岛（Kingdom 实例变化）
        private static float _nextTimePauseCheck;             // 时间停滞检测节流时间
        private static float _lastDirectorTime = -1f;         // 上次采样到的游戏时刻（判断时钟是否真的不走）
        private static float _timeStuckSince = -1f;           // 时钟连续停滞的起点（-1 表示时钟正常）
        private static int _timeStuckRepairs;                 // 停滞修补轮次：0=先恢复时钟与状态，1=仍不动则清理残留时间停止者

        // 城墙边界缓存（供弓箭手守墙bug修正使用）
        private struct WallBound { public float X; public float HalfW; public bool UnderConstruction; }
        private static readonly List<WallBound> _wallBounds = new();

        // 禁用排队机制：记录钳制前的原始排队值（键为对象实例ID），关闭开关时按原值立即还原
        private static readonly Dictionary<int, int> _savedGuardDepth = new();
        private static readonly Dictionary<int, int> _savedKnightRank = new();
        private bool _lastDisableWallQueue;
        private static float _nextWorkBalance;        // 下一次排队工作就近改派时刻
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Worker> _balanceWorkerBuffer;
        // 就近改派复用的“空闲候选”池：每轮只筛一次，避免对每个排队工作重复做互操作状态查询（O(n²)）
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Worker> _balanceIdleBuffer;
        private static float[] _balanceIdleX;
        // 传送门被毁后改派驻守侧时复用的工人缓冲
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Worker> _stationWorkerBuffer;
        private static float _nextStationSideCheck;   // 下一次传送门驻守侧检测时刻（周期检测）
        // 周期性遍历复用缓冲：修正流程每 2 秒执行一次，反复分配 IL2CPP 数组会造成 GC 尖峰与掉帧
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Archer> _archerBuffer;
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Wall> _wallBuffer;
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<GuardSlot> _guardSlotBuffer;
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Knight> _knightBuffer;
        // 攻门（付费冲锋）小队优先级：PayableBorder.Pay 遍历骑士时只放行选中的那支小队
        private static int _chargeBestId = -1;          // 选中小队的实例 ID（IL2CPP 实例 ID 常为负数）
        private static bool _chargeHasPick;             // 是否已选中可用小队（不能用 ID 正负判断）
        private static bool _chargePickActive;          // 处于 PayableBorder.Pay 执行期间
        private static bool _chargePicking;             // 正在计算最佳小队（绕过 CanCharge 过滤）

        // 全局重型刷新槽位：本组件与地图覆盖层的周期刷新共用，同一帧只允许一个重型任务执行，
        // 且相邻两个重型任务之间至少空出 HeavySlotGapFrames 帧，避免多个全场景扫描/遍历叠在连续帧上形成帧尖峰
        private static int _heavySlotFrame = -1000;

        // 摇铃立即上船的待处理队列：延后一帧再 Embark，避开读档恢复乘客时游戏自身紧接着的 Embark。
        // 同一单位连续两次 Embark 会各占一个座位（第二次占座不释放），下船时重复处理导致整船人卡死。
        internal static readonly List<Embarkee> _pendingInstantBoards = new List<Embarkee>();

        // ---- 摇铃选人窗口 ----
        // 玩家付钱摇铃时先弹出选人窗口，确认后再按所选人数放行游戏原生的登船分配。
        // 配额只作用于本次摇铃的那艘船：原生分配在给单位指派上船目标时会被拦截，
        // 超过所选数量的单位不指派目标，因此不会出现多带人的情况。
        private static Boat _bellSelectBoat;                 // 正在选人的船（窗口打开期间有效）
        internal static bool _bellSummonFromPay;             // 本次 OnBoatSummonBellRang 是否由摇铃付钱触发
        internal static bool _bellAllowNativeSummon;         // 确认/取消后放行一次原生摇铃
        internal static bool _bellQuotaActive;               // 配额是否生效（确认后到下次摇铃）
        private static Embarkable _bellQuotaEmbarkable;      // 配额作用的登船点（本次摇铃的船）
        private static int _bellQuotaWorkers = -1;           // 工人上限（-1 表示不限制）
        private static int _bellQuotaArchers = -1;           // 弓箭手上限（只统计非骑士随从）
        private static int _bellQuotaKnights = -1;           // 骑士小队上限
        private static readonly HashSet<int> _bellQuotaWorkerIds = new HashSet<int>();
        private static readonly HashSet<int> _bellQuotaArcherIds = new HashSet<int>();
        private static readonly HashSet<int> _bellQuotaKnightIds = new HashSet<int>();

        /// <summary>
        /// 申请本帧的重型刷新槽位。返回 false 表示本帧或上一帧已有其它重型任务在执行，
        /// 调用方不要重置自己的计时器，顺延到后续帧即可（各任务周期远大于一帧，不会饿死）。
        /// 帧间隔保证：两个重型任务之间至少空出 HeavySlotGapFrames 帧，把重活摊平到多帧。
        /// </summary>
        public static bool TryReserveHeavySlot()
        {
            int frame = Time.frameCount;
            if (frame - _heavySlotFrame <= HeavySlotGapFrames) return false;
            _heavySlotFrame = frame;
            return true;
        }

        private void Awake()
        {
            _lastDisableWallQueue = ModWindowBehaviour.DisableWallQueue;
            // 错峰初始化各周期任务：首帧若全部到期会连续多帧排队，这里按不同相位散开
            float now = Time.unscaledTime;
            _nextSafetyBoundsScan = now;
            _nextCacheRefresh = now + 0.1f;
            _nextTimePauseCheck = now + 0.5f;
            _nextWallQueueClamp = now + 0.6f;
            _nextHeavyWork = now + 0.7f;
            _nextWorkBalance = now + 0.9f;
        }

        private void Update()
        {
            if (!IsOverlayVisible())
            {
                // 读档/加载期间游戏会自己完成 Embark，本帧入队的请求已无意义，直接丢弃
                _pendingInstantBoards.Clear();
                // 读档/加载时游戏会自行恢复乘客，本次摇铃的配额不能再拦截这些原生指派
                _bellQuotaActive = false;
                _bellQuotaEmbarkable = null;
                ClearBellQuotaUnits();
                return;
            }
            ProcessPendingInstantBoards();
            _isMenuOpen = IsMenuOpen();
            SyncWallQueueSwitch();

            float now = Time.unscaledTime;

            // 城墙边界刷新：建墙/破墙后 0.5 秒内反映到避怪阈值计算（ShouldFlee 强制补丁用）
            if (now >= _nextSafetyBoundsScan && TryReserveHeavySlot())
            {
                _nextSafetyBoundsScan = now + SafetyBoundsScanInterval;
                RefreshWallBounds();
            }

            // 危险敌人扫描：Worker/Archer 自带的 Scanner 探测半径固定约 6，fleeRange 写再大也看不见远处的敌人；
            // 这里用 EnemyManager.AllEnemies 自行扫描缓存，ShouldFlee 补丁只查缓存做浮点比较（0.3 秒刷新一次）
            if (now >= _nextFleeEnemyScan && TryReserveHeavySlot())
            {
                _nextFleeEnemyScan = now + FleeEnemyScanInterval;
                RefreshFleeEnemies();
            }

            // 排队工作就近改派：原版把新工作交给先入队的空闲工人，这里改成离工作点最近的空闲工人
            if (now >= _nextWorkBalance && TryReserveHeavySlot())
            {
                _nextWorkBalance = now + WorkBalanceInterval;
                RebalanceQueuedWork();
            }

            if (now >= _nextCacheRefresh && TryReserveHeavySlot())
            {
                _nextCacheRefresh = now + CacheInterval;
                RefreshCaches();
            }

            // 血月结束后游戏可能把时钟倍率留在 0，导致时间永久停住（只有重进游戏才恢复）
            if (now >= _nextTimePauseCheck)
            {
                _nextTimePauseCheck = now + 1f;
                EnsureTimeNotStuck(now);
            }

            // 禁用排队时的兜底复钳：游戏会在夜袭等时机经其它分配路径重新写入排队值；
            // 分配补丁（Archer/Knight 分派与 RankKnights）已经即时复钳，这里只低频兜底
            if (ModWindowBehaviour.DisableWallQueue && now >= _nextWallQueueClamp && TryReserveHeavySlot())
            {
                _nextWallQueueClamp = now + WallQueueClampInterval;
                ClampWallQueueing();
            }

            // 钱袋国库联动（每帧：溢出即时存入，长按取款需帧级按键追踪）
            UpdatePurseTreasuryLink(now);

            // 传送门被摧毁后（某侧已无传送门）把长期驻守在该侧的工人改派到另一侧，只改驻守侧。
            // 恢复为周期检测：每 2 秒检查一次，只有“恰好一侧没有传送门”时才会写工人，其余时刻几乎零开销
            if (now >= _nextStationSideCheck && TryReserveHeavySlot())
            {
                _nextStationSideCheck = now + StationSideCheckInterval;
                ReassignStationSideAfterPortalDestroyed();
            }

            if (now >= _nextHeavyWork && TryReserveHeavySlot())
            {
                _nextHeavyWork = now + HeavyInterval;
                FixWallDefensePositions();
            }
        }

        /// <summary>
        /// 处理“摇铃立即上船”的待登船单位：延后一帧，让读档恢复乘客时游戏自身的 Embark 先执行。
        /// 若单位已被游戏登船、目标已失效或已不是船，就跳过，避免重复 Embark 造成重复占座。
        /// </summary>
        private static void ProcessPendingInstantBoards()
        {
            if (_pendingInstantBoards.Count == 0) return;
            if (!ModWindowBehaviour.BellInstantBoard)
            {
                _pendingInstantBoards.Clear();
                return;
            }
            for (int i = _pendingInstantBoards.Count - 1; i >= 0; i--)
            {
                Embarkee embarkee = _pendingInstantBoards[i];
                _pendingInstantBoards.RemoveAt(i);
                if (embarkee == null) continue;
                try
                {
                    if (embarkee.IsEmbarked) continue;
                    Embarkable target = embarkee.EmbarkableTarget;
                    if (target == null || target._owner == null || target._owner.TryCast<Boat>() == null) continue;
                    embarkee.Embark();
                }
                catch { }
            }
        }

        // ================= 摇铃选人 =================

        /// <summary>打开摇铃选人窗口：读取当前岛上三类职业的实际人数，交给设置窗口显示。</summary>
        internal static bool BeginBellSelection(Boat boat)
        {
            if (boat == null) return false;
            if (ModWindowBehaviour.IsOpen) return false;   // 设置窗口已打开时不再叠加窗口
            Kingdom kingdom = TryGetKingdom();
            if (kingdom == null) return false;

            int maxWorkers = 0, maxArchers = 0, maxKnights = 0;
            try { if (kingdom.Workers != null) maxWorkers = kingdom.Workers.Count; } catch { }
            try { maxArchers = CountFreeArchers(kingdom); } catch { }
            try { if (kingdom.Knights != null) maxKnights = kingdom.Knights.Count; } catch { }

            _bellSelectBoat = boat;
            _bellQuotaActive = false;
            ClearBellQuotaUnits();
            // 显示失败（原生 UI 尚未就绪等）时返回 false，调用方放行原生摇铃，避免吞掉付钱效果
            if (!ModWindowBehaviour.ShowSailorSelect(maxWorkers, maxArchers, maxKnights))
            {
                _bellSelectBoat = null;
                return false;
            }
            return true;
        }

        /// <summary>玩家在窗口点“确定”：按选择调整船的席位数量，再执行游戏原生摇铃分配。</summary>
        internal static void ConfirmBellSelection(int workers, int archers, int knights)
        {
            Boat boat = _bellSelectBoat;
            _bellSelectBoat = null;
            if (boat == null) return;

            bool slotsAdjusted = false;
            try
            {
                AdjustBellSlots(boat, workers, archers, knights);
                slotsAdjusted = true;
            }
            catch { }

            try
            {
                // 原生分配在 OnBoatSummonBellRang 内同步完成（ToggleAllBoardingPermissions → DistributeEmbarkeesOfType），
                // 配额只需覆盖这一次同步调用；调用结束立即清除，避免影响读档/返回旧岛时的原生指派。
                if (slotsAdjusted)
                {
                    _bellQuotaEmbarkable = boat._embarkable;
                    _bellQuotaWorkers = Mathf.Max(0, workers);
                    _bellQuotaArchers = Mathf.Max(0, archers);
                    _bellQuotaKnights = Mathf.Max(0, knights);
                    ClearBellQuotaUnits();
                    _bellQuotaActive = true;
                }
                InvokeNativeBellSummon(boat);
            }
            catch { }
            finally
            {
                _bellQuotaActive = false;
                _bellQuotaEmbarkable = null;
                ClearBellQuotaUnits();
            }
        }

        /// <summary>窗口被强制关闭（游戏离开游玩状态）时丢弃本次选择，不执行原生召唤。</summary>
        internal static void AbortBellSelection()
        {
            _bellSelectBoat = null;
            _bellQuotaActive = false;
            _bellQuotaEmbarkable = null;
            ClearBellQuotaUnits();
        }

        /// <summary>放行一次原生摇铃；放行标志避免被本模组自己的 Prefix 再次拦截。</summary>
        private static void InvokeNativeBellSummon(Boat boat)
        {
            if (boat == null) return;
            _bellAllowNativeSummon = true;
            try { boat.OnBoatSummonBellRang(); }
            catch { }
            finally { _bellAllowNativeSummon = false; }
        }

        /// <summary>
        /// 按选择调整船的登船席位：原生主船只有 3 工人、4 弓箭手、3 骑士小队。
        /// 超过原生容量时在席位列表尾部追加 EmbarkeeSlot；少于原生容量时移除尾部空闲席位。
        /// 骑士随从以 Stowaway（slotId=-1）方式随骑士上船，不占用弓箭手席位，因此无需为其预留。
        /// </summary>
        private static void AdjustBellSlots(Boat boat, int workers, int archers, int knights)
        {
            Embarkable embarkable = boat != null ? boat._embarkable : null;
            if (embarkable == null) return;
            EnsureSlotCapacity(embarkable, "Worker", Mathf.Max(0, workers));
            EnsureSlotCapacity(embarkable, "Archer", Mathf.Max(0, archers));
            EnsureSlotCapacity(embarkable, "Knight", Mathf.Max(0, knights));
        }

        /// <summary>把某职业的席位数量调整到 desired（尾部增删，已占用的席位不删除）。</summary>
        private static void EnsureSlotCapacity(Embarkable embarkable, string typeName, int desired)
        {
            if (embarkable == null || desired < 0) return;
            try
            {
                Il2CppSystem.Type key;
                Il2CppSystem.Collections.Generic.List<EmbarkeeSlot> slots;
                // 类型未注册席位时不做处理：主船在 OnEnable 中已为 Worker/Archer/Knight 注册原生席位，
                // 这里只调整已有列表，避免重复注册造成状态不一致。
                if (!TryFindSlotList(embarkable, typeName, out key, out slots)) return;
                if (slots == null) return;

                // 删除多余尾部席位：只删除空席位，避免把已经登船的单位孤立在船外
                while (slots.Count > desired)
                {
                    EmbarkeeSlot last = slots[slots.Count - 1];
                    if (last == null || last._occupant != null || last.IsOccupied) break;
                    slots.RemoveAt(slots.Count - 1);
                }
                // 追加额外席位（多出的人“另外申请席位”）
                for (int i = slots.Count; i < desired; i++)
                    slots.Add(new EmbarkeeSlot(embarkable, key, i));
            }
            catch { }
        }

        /// <summary>在船的席位字典里按职业名找到对应的席位列表。</summary>
        private static bool TryFindSlotList(Embarkable embarkable, string typeName,
            out Il2CppSystem.Type key, out Il2CppSystem.Collections.Generic.List<EmbarkeeSlot> slots)
        {
            key = null;
            slots = null;
            try
            {
                var dict = embarkable._slots;
                if (dict == null) return false;
                var keys = dict.Keys;
                if (keys == null) return false;
                int count = keys.Count;
                if (count <= 0) return false;
                var keyArray = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Type>(count);
                keys.CopyTo(keyArray, 0);
                for (int i = 0; i < count; i++)
                {
                    Il2CppSystem.Type t = keyArray[i];
                    if (t == null) continue;
                    string name = null;
                    try { name = t.Name; } catch { }
                    if (name == typeName)
                    {
                        key = t;
                        slots = dict[t];
                        return slots != null;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>当前岛上不属于骑士小队的自由弓箭手数量（骑士随从由骑士上船时自动携带）。</summary>
        private static int CountFreeArchers(Kingdom kingdom)
        {
            int count = 0;
            try
            {
                var archers = kingdom.Archers;
                if (archers == null) return 0;
                int total = archers.Count;
                var buffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Archer>(total);
                archers.CopyTo(buffer, 0);
                for (int i = 0; i < total; i++)
                {
                    Archer archer = buffer[i];
                    if (archer == null) continue;
                    try { if (archer.isKnightSoldier) continue; } catch { }
                    count++;
                }
            }
            catch { }
            return count;
        }

        private static Kingdom TryGetKingdom()
        {
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                return managers != null ? managers.kingdom : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 登船配额过滤：只放行所选数量以内的工人/自由弓箭手/骑士。
        /// 骑士随从弓箭手（isKnightSoldier）随骑士行动，不占“弓箭手”名额。
        /// 返回 false 时游戏不会给该单位指派上船目标，单位留在岸上。
        /// </summary>
        internal static bool AcceptBellUnit(int kind, Embarkee embarkee)
        {
            if (embarkee == null) return true;
            int id;
            HashSet<int> taken;
            int limit;
            switch (kind)
            {
                case 0: taken = _bellQuotaWorkerIds; limit = _bellQuotaWorkers; break;
                case 1: taken = _bellQuotaArcherIds; limit = _bellQuotaArchers; break;
                case 2: taken = _bellQuotaKnightIds; limit = _bellQuotaKnights; break;
                default: return true;
            }
            if (limit < 0) return true;
            try { id = embarkee.GetInstanceID(); } catch { return true; }
            if (taken.Contains(id)) return true;          // 同一单位重复指派不重复占名额
            if (taken.Count >= limit) return false;
            taken.Add(id);
            return true;
        }

        private static void ClearBellQuotaUnits()
        {
            _bellQuotaWorkerIds.Clear();
            _bellQuotaArcherIds.Clear();
            _bellQuotaKnightIds.Clear();
        }

        /// <summary>
        /// 配额判定入口（Harmony Prefix 调用）：只在本次摇铃的那艘船上生效。
        /// 骑士随从弓箭手随骑士上船，不占“弓箭手”名额。
        /// </summary>
        internal static bool BellQuotaAllows(Embarkee embarkee, Embarkable embarkable)
        {
            if (!_bellQuotaActive) return true;
            if (embarkee == null || embarkable == null) return true;
            if (embarkable != _bellQuotaEmbarkable) return true;
            try
            {
                string typeName = null;
                try { typeName = embarkee.UnitType != null ? embarkee.UnitType.Name : null; } catch { }
                if (typeName == "Worker") return AcceptBellUnit(0, embarkee);
                if (typeName == "Knight") return AcceptBellUnit(2, embarkee);
                if (typeName == "Archer")
                {
                    Archer archer = embarkee.GetComponent<Archer>();
                    bool knightSoldier = false;
                    try { knightSoldier = archer != null && archer.isKnightSoldier; } catch { }
                    if (knightSoldier) return true;   // 骑士随从由骑士携带，不占自由弓箭手名额
                    return AcceptBellUnit(1, embarkee);
                }
                return true;
            }
            catch { return true; }
        }

        /// <summary>
        /// 检测换岛（Kingdom 实例变化）：旧岛的墙体缓存、已取消工作全部作废，
        /// 避免拿旧岛数据判断新岛。
        /// </summary>
        private void TrackKingdomChange(Kingdom kingdom)
        {
            try
            {
                int kid = kingdom.GetInstanceID();
                if (kid == _cachedKingdomId) return;
                _cachedKingdomId = kid;
                _wallBounds.Clear();
                _nextWallBoundsScan = 0f;        // 新岛必须立刻重建墙体边界
                _nextSafetyBoundsScan = 0f;
                _nextFleeEnemyScan = 0f;
                _fleeEnemyX.Clear();
                _stickyFleeWorkers.Clear();      // 旧岛工人的避怪粘滞标记作废（对象已销毁）
                _stickyFleeArchers.Clear();      // 旧岛弓箭手的避怪粘滞标记作废（对象已销毁）
                _pendingInstantBoards.Clear();
                _bellQuotaActive = false;
                _bellQuotaEmbarkable = null;
                ClearBellQuotaUnits();
                float now = Time.unscaledTime;
                _nextHeavyWork = now + 0.7f;     // 换岛后错峰，避免与其它重型任务同帧执行
                _nextStationSideCheck = 0f;      // 换岛/读档后立即做一次驻守侧改派，避免工人先跑向旧驻守侧
            }
            catch { }
        }

        private void RefreshCaches()
        {
            _cachedPlayer = null;
            _cachedPlayer2 = null;
            _cachedCastle = null;
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                if (managers == null) return;
                Kingdom kingdom = managers.kingdom;
                if (kingdom == null) return;

                Player player = kingdom.playerOne ?? kingdom.GetPlayer(0);
                _cachedPlayer = player;
                // 分屏玩家2（单人游戏 GetPlayer(1) 返回空或玩家1本身，后者需剔除）
                try
                {
                    _cachedPlayer2 = kingdom.GetPlayer(1);
                    if (_cachedPlayer2 != null && player != null && _cachedPlayer2.playerId == player.playerId)
                        _cachedPlayer2 = null;
                }
                catch { _cachedPlayer2 = null; }

                _cachedCastle = kingdom.castle;
                TrackKingdomChange(kingdom);

                // 玩家最高移速提升（功能开关）：开启时抬速并记录原始值，关闭时还原
                SyncPlayerSpeedBoost(player, _cachedPlayer2);
            }
            catch { }

            // 银行家（税官）缓存：FindObjectsByType(含隐藏对象) 是全场景遍历，
            // 由 ResolveBanker 分享给地图覆盖层并做 2 秒限频，避免两边各每 0.25s 扫一次场景
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                _cachedBanker = ResolveBanker(kingdom);
            }
            catch { _cachedBanker = null; }
        }

        /// <summary>
        /// 解析银行家（税官）：优先 kingdom.banker，其次场景查找（含隐藏对象）兜底。
        /// 场景查找是全场景遍历，这里共用一份缓存并限频（BankerScanInterval），
        /// 本组件与地图覆盖层都通过它取税官，避免两边各每 0.25s 扫描一次造成周期性卡顿。
        /// </summary>
        internal static Banker ResolveBanker(Kingdom kingdom)
        {
            int kingdomId = -1;
            try { if (kingdom != null) kingdomId = kingdom.GetInstanceID(); } catch { }
            if (kingdomId != _sharedBankerKingdomId)
            {
                // 换岛：旧岛的税官引用作废，立刻允许下一次场景查找
                _sharedBankerKingdomId = kingdomId;
                _sharedBanker = null;
                _nextBankerScan = 0f;
            }

            // 缓存对象已被销毁时丢弃（不占用限频窗口，下一次调用可立即重找）
            if (_sharedBanker != null)
            {
                try { if (_sharedBanker.gameObject != null) return _sharedBanker; } catch { }
                _sharedBanker = null;
            }
            if (kingdom == null) return null;

            try { _sharedBanker = kingdom.banker; } catch { _sharedBanker = null; }
            if (_sharedBanker != null)
            {
                try { if (_sharedBanker.gameObject != null) return _sharedBanker; } catch { }
                _sharedBanker = null;
            }

            float now = Time.unscaledTime;
            if (now < _nextBankerScan) return null;
            _nextBankerScan = now + BankerScanInterval;
            try
            {
                var bankers = UnityEngine.Object.FindObjectsByType<Banker>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (bankers.Length > 0) _sharedBanker = bankers[0];
            }
            catch { }
            return _sharedBanker;
        }

        // 开启移速提升前各坐骑的原始奔跑速度（关闭开关时按原值还原）
        private struct BoostedSteed { public int Id; public Steed Steed; public float BaseRunSpeed; public float BoostedRunSpeed; }
        private static readonly List<BoostedSteed> _boostedSteeds = new List<BoostedSteed>();

        /// <summary>
        /// 按“玩家移动速度提升”开关同步移速：开启时把当前坐骑的奔跑速度提升到原值的 150%（+50%），
        /// 关闭时把之前改过的坐骑全部还原，保证开关即时生效。
        /// </summary>
        private static void SyncPlayerSpeedBoost(Player p1, Player p2)
        {
            if (ModWindowBehaviour.BoostPlayerSpeed)
            {
                BoostPlayerRunSpeed(p1);
                BoostPlayerRunSpeed(p2);
                return;
            }
            if (_boostedSteeds.Count == 0) return;

            // 正在奔跑的目标速度也退回原值（只认我们写入的精确值，避免误伤冲刺等更高速度）
            RestorePlayerMoverSpeed(p1);
            RestorePlayerMoverSpeed(p2);
            for (int i = 0; i < _boostedSteeds.Count; i++)
            {
                try
                {
                    Steed s = _boostedSteeds[i].Steed;
                    if (s == null) continue;
                    float cur = s.runSpeed;
                    // 只还原落在 [原值, 提升值] 之间的数值，避免覆盖其他来源的更高特殊速度
                    if (cur >= _boostedSteeds[i].BaseRunSpeed - 0.05f && cur <= _boostedSteeds[i].BoostedRunSpeed + 0.05f)
                        s.runSpeed = _boostedSteeds[i].BaseRunSpeed;
                }
                catch { }
            }
            _boostedSteeds.Clear();
        }

        /// <summary>
        /// 把玩家的奔跑速度提升到当前坐骑基础值的 150%：
        /// 直接改坐骑的 runSpeed，让游戏下一次进入奔跑时按新速度走；
        /// 同时把当前已生效的移动目标速度抬到提升值，避免正在奔跑的这一段仍停留在旧速度。
        /// 只处理“不低于该坐骑原奔跑速度”的移动——行走速度远低于原值，冲刺等更高特殊速度也一律不动。
        /// </summary>
        private static void BoostPlayerRunSpeed(Player player)
        {
            if (player == null) return;
            float baseSpeed = 0f, target = 0f;
            bool boosted = false;
            try
            {
                Steed steed = player.steed;
                if (steed != null)
                {
                    baseSpeed = GetSteedBaseRunSpeed(steed, steed.runSpeed);
                    target = baseSpeed * PlayerRunSpeedBoostRatio;
                    // 已是更高特殊速度时不改写，避免覆盖游戏自身的加速效果
                    if (target > baseSpeed + 0.01f && steed.runSpeed <= target + 0.05f)
                    {
                        RememberSteedBaseSpeed(steed, baseSpeed, target);
                        if (Math.Abs(steed.runSpeed - target) > 0.01f) steed.runSpeed = target;
                        boosted = true;
                    }
                }
            }
            catch { }
            if (!boosted) return;
            try
            {
                Mover mover = player.mover;
                // 只在真正按住奔跑键（Run 态）时改当前速度：松开 Shift 后玩家进入 Glide(9) 滑行态，
                // 原生 isRunning 在该态仍为 true（速度 > glidePayThreshold 才算奔跑），若继续抬速会导致
                // 速度永远高于阈值、卡死在奔跑。Glide 期间不抬速，让速度自然衰减恢复行走。
                if (mover != null && player.actionState == Player.ActionState.Run)
                {
                    float speed = mover._moveSpeed;
                    // 仅把“还停在原奔跑速度”的那一段抬到提升值；已经是提升值则不重复写，
                    // 松开冲刺键后游戏把速度切回行走值时也绝不把它改回奔跑速度
                    if (Math.Abs(speed - baseSpeed) <= 0.05f)
                        mover._moveSpeed = target;
                }
            }
            catch { }
        }

        /// <summary>取坐骑被提升前的原始奔跑速度（未记录过则视为当前值）。</summary>
        private static float GetSteedBaseRunSpeed(Steed steed, float current)
        {
            try
            {
                int id = steed.GetInstanceID();
                for (int i = 0; i < _boostedSteeds.Count; i++)
                    if (_boostedSteeds[i].Id == id) return _boostedSteeds[i].BaseRunSpeed;
            }
            catch { }
            return current;
        }

        /// <summary>记录坐骑被改动前的原始奔跑速度与提升后的目标值（同一坐骑只记录一次）。</summary>
        private static void RememberSteedBaseSpeed(Steed steed, float baseSpeed, float boostedSpeed)
        {
            try
            {
                int id = steed.GetInstanceID();
                for (int i = 0; i < _boostedSteeds.Count; i++)
                {
                    if (_boostedSteeds[i].Id != id) continue;
                    BoostedSteed rec = _boostedSteeds[i];
                    rec.BoostedRunSpeed = boostedSpeed;
                    _boostedSteeds[i] = rec;
                    return;
                }
                if (_boostedSteeds.Count >= 8) return;   // 正常只会接触几匹马，上限防止列表无限增长
                _boostedSteeds.Add(new BoostedSteed { Id = id, Steed = steed, BaseRunSpeed = baseSpeed, BoostedRunSpeed = boostedSpeed });
            }
            catch { }
        }

        /// <summary>关闭开关时把玩家当前奔跑目标速度退回该坐骑的原始值。</summary>
        private static void RestorePlayerMoverSpeed(Player player)
        {
            if (player == null) return;
            try
            {
                Steed steed = player.steed;
                Mover mover = player.mover;
                if (steed == null || mover == null) return;
                int id = steed.GetInstanceID();
                for (int i = 0; i < _boostedSteeds.Count; i++)
                {
                    if (_boostedSteeds[i].Id != id) continue;
                    BoostedSteed rec = _boostedSteeds[i];
                    if (Math.Abs(mover._moveSpeed - rec.BoostedRunSpeed) <= 0.05f)
                        mover._moveSpeed = rec.BaseRunSpeed;
                    return;
                }
            }
            catch { }
        }

        /// <summary>钱袋国库联动（每帧调用）：有效金币上限随钻石数下降，超出部分自动存入国库；空袋长按支付键取款填满。</summary>
        private void UpdatePurseTreasuryLink(float now)
        {
            if (!ModWindowBehaviour.LinkPurseTreasury) return;
            Castle castle = _cachedCastle;
            if (castle == null) return;   // 尚无城堡时不做任何转移
            // 国库未解锁（还没有银行家）时没有真实账本：
            // Castle._currentCoinCount 只是城堡金币堆的显示镜像（由 SendStashCount RPC 同步），不参与存档，
            // 写进去金币会从钱袋消失却不落账，读出来还会凭空造币。因此没有银行家时整个联动不生效
            Banker banker = _cachedBanker;
            if (banker == null) return;
            HandlePurseLink(_cachedPlayer, 0, castle, banker, now, ref _payHoldStartP1);
            HandlePurseLink(_cachedPlayer2, 1, castle, banker, now, ref _payHoldStartP2);
        }

        /// <summary>读国库账本：优先税官账本（Banker._stashedCoins），无税官时回退城堡显示字段。</summary>
        internal static int ReadTreasury(Banker banker, Castle castle)
        {
            if (banker != null) { try { return banker._stashedCoins; } catch { } }
            try { return castle._currentCoinCount; } catch { return 0; }
        }

        /// <summary>
        /// 写国库账本：税官账本 + 城堡金币堆显示同步刷新。
        /// 必须调用 Castle.SetStash 立即重绘金币堆——只写字段/CallSendStashCount 时，
        /// 画面要等游戏自己花钱或税官下次存钱才更新，表现为“国库显示不随取款/存款变化”。
        /// </summary>
        private static void WriteTreasury(Banker banker, Castle castle, int value)
        {
            if (banker != null) { try { banker._stashedCoins = value; } catch { } }
            try { castle._currentCoinCount = value; } catch { }
            try { castle.SetStash(value); } catch { }
            try { castle.SendStashCount(); } catch { }
        }

        /// <summary>单个玩家的钱袋联动：先处理溢出存入，再处理空袋长按取款。</summary>
        private void HandlePurseLink(Player p, int playerIndex, Castle castle, Banker banker, float now, ref float holdStart)
        {
            if (p == null) { holdStart = -1f; return; }
            int coins, gems;
            Wallet wallet;
            try { coins = p.coins; gems = p.gems; wallet = p.wallet; } catch { holdStart = -1f; return; }
            // 钻石占钱袋容量（1 颗钻石 = 3 金币位），有效金币上限 = 38 - 3×钻石数，下限 0
            int maxCoins = Math.Max(0, PurseMaxCoins - 3 * gems);

            // 溢出存入：钱袋金币超过有效上限时把多余部分装入国库
            if (coins > maxCoins)
            {
                int excess = coins - maxCoins;
                try
                {
                    int stash = ReadTreasury(banker, castle);
                    wallet.RemoveCurrency(CurrencyType.Coins, excess);
                    WriteTreasury(banker, castle, stash + excess);
                    coins = maxCoins;
                }
                catch { }
            }

            // 空袋取款：菜单/MOD 窗口打开时游戏输入被屏蔽，不判定按键。
            // 只在松开按键时重置计时：按住支付键移动时钱袋会被陆续花空，
            // 若“袋里有币”也重置计时，空袋后还要重新等阈值，表现为移动中支付不跟手。
            // 防误触语义保留：刚按下时仍要按住超过阈值，短点击不会取款。
            bool held = !_isMenuOpen && !ModWindowBehaviour.IsOpen && SafePayHeld(playerIndex);
            if (!held) { holdStart = -1f; return; }                 // 松键/输入屏蔽后重置，下次取款需重新长按
            if (holdStart < 0f) holdStart = now;                    // 按下即开始计时（袋里有币也累计，空袋瞬间即可取款）
            if (coins > 0) return;                                  // 袋里有币：保留已累计的长按时间
            if (now - holdStart < PayHoldThreshold) return;         // 未达阈值（短点击不触发）
            // 触发后不清零：持续按住时，后续每次花空钱袋都能立即补款（移动中跟手）
            try
            {
                int stash = ReadTreasury(banker, castle);
                if (stash <= 0) return;
                // 取出量 = 填满有效上限所需金币数（受钻石占容量影响），国库不足则全取
                int take = Math.Min(maxCoins, stash);
                if (take <= 0) return;
                WriteTreasury(banker, castle, stash - take);
                wallet.AddCurrency(CurrencyType.Coins, take);
            }
            catch { }
        }

        /// <summary>安全读取指定玩家的支付键按住状态（互操作/配置异常时视为未按）。</summary>
        private static bool SafePayHeld(int playerIndex)
        {
            try { return HotkeyBindings.IsPayKeyHeld(playerIndex); } catch { return false; }
        }

        // ==================== 墙体内部守墙修正 ====================

        /// <summary>
        /// 游戏原生bug修正入口：弓箭手守墙待机位有时会位于墙体内部。纠错类任务按 HeavyInterval 低频执行。
        /// 禁用排队的复钳由分配补丁即时触发、另有 WallQueueClampInterval 兜底，这里不再重复执行。
        /// </summary>
        private void FixWallDefensePositions()
        {
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                if (kingdom == null) return;

                // 墙体只在建造/被毁时变化，边界缓存按 WallBoundsScanInterval 重建即可
                float now = Time.unscaledTime;
                if (_wallBounds.Count == 0 || now >= _nextWallBoundsScan)
                {
                    CollectWallBounds(kingdom);
                    _nextWallBoundsScan = now + WallBoundsScanInterval;
                }
                if (_wallBounds.Count == 0) return;
                // 需要城堡位置判断"墙后"是哪一侧
                if (!TryGetX(kingdom.castle, out float castleX)) return;

                FixGuardSlotsInsideWalls(kingdom, castleX);
                FixArchersInsideWalls(kingdom, castleX);
            }
            catch { }
        }

        /// <summary>
        /// 复仇血月/黑暗收尾兜底：攻破传送门后游戏会暂停时钟并切到红月/黑暗画面，
        /// 正常由 EnemyManager.CheckDanger 在“无怪物攻城 + 无待出波次 + 王国处于非安全”时执行
        /// Director.ResumeTime() 与 ClearRedMoon()/ClearDarkness() 收尾。
        /// 一旦该分支被跳过（例如王国被提前转成安全态），时钟与画面就会永久卡在血月进攻状态，
        /// 只有重进游戏才恢复。这里检测“红月/黑暗标记仍在 + 游戏自身已无危险 + 时钟连续数秒不动”，
        /// 主动补齐收尾流程；若恢复倍率后时钟仍不走，再清理残留的时间停止者。
        /// </summary>
        private static void EnsureTimeNotStuck(float now)
        {
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                if (managers == null) return;
                Director director = managers.director;
                if (director == null) return;
                EnemyManager enemies = managers.enemies;

                float time;
                try { time = director.currentTime; } catch { return; }
                if (_lastDirectorTime < 0f) { _lastDirectorTime = time; _timeStuckSince = -1f; return; }
                bool moving = Mathf.Abs(time - _lastDirectorTime) > 0.0001f;
                _lastDirectorTime = time;

                bool redMoon = false, darkness = false, dangerous = true, playing = true;
                try
                {
                    if (enemies != null)
                    {
                        redMoon = enemies.isRedMoon;
                        darkness = enemies.isDarkness;
                        dangerous = enemies.IsDangerous;
                    }
                }
                catch { dangerous = true; }
                try { playing = director.playing; } catch { }

                // 时钟正常推进，或游戏本身处于暂停（菜单/演出/网络同步）时不做任何事
                if (moving || !playing || Time.timeScale <= 0f)
                {
                    _timeStuckSince = -1f;
                    _timeStuckRepairs = 0;
                    return;
                }
                if (!redMoon && !darkness) { _timeStuckSince = -1f; _timeStuckRepairs = 0; return; }
                // 还有怪物攻城或有待出的复波次时属于正常血月战斗（游戏会主动暂停时钟），不干预
                if (dangerous) { _timeStuckSince = -1f; return; }

                if (_timeStuckSince < 0f) { _timeStuckSince = now; return; }
                // 连续 10 秒无危险且时钟不动才介入：留出正常波次间隔与收尾演出时间
                if (now - _timeStuckSince < 10f) return;

                if (_timeStuckRepairs == 0)
                {
                    // 第一轮：补齐原生收尾（恢复时钟倍率 + 清除红月/黑暗状态，含天空与材质）
                    try { director.ResumeTime(); } catch { }
                    try { enemies.ClearRedMoon(); } catch { }
                    try { enemies.ClearDarkness(); } catch { }
                    try
                    {
                        Kingdom kingdom = managers.kingdom;
                        if (kingdom != null) kingdom.CheckBecomeSafe();
                    }
                    catch { }
                    _timeStuckRepairs = 1;   // 若 10 秒后时钟仍不动，下一轮再清理残留的时间停止者
                }
                else
                {
                    // 第二轮：恢复倍率后时钟依旧不走，说明导演的时间停止者列表里还留着 TimeStopper
                    // （它会让 Director.Update 整段提前返回）。只从列表注销，不销毁/禁用视觉对象。
                    try
                    {
                        var stoppers = director._timeStoppers;
                        if (stoppers != null && stoppers.Count > 0)
                        {
                            for (int i = stoppers.Count - 1; i >= 0; i--)
                            {
                                TimeStopper stopper = stoppers[i];
                                if (stopper == null) continue;
                                try { director.RemoveTimeStopper(stopper); } catch { }
                            }
                        }
                    }
                    catch { }
                    _timeStuckRepairs = 0;
                }
                _timeStuckSince = -1f;
            }
            catch { }
        }

        // ==================== 守墙排队禁用 ====================

        /// <summary>开关翻转检测：开启时立即钳制排队站位，关闭时立即还原游戏原生排队。</summary>
        private void SyncWallQueueSwitch()
        {
            bool disable = ModWindowBehaviour.DisableWallQueue;
            if (disable == _lastDisableWallQueue) return;
            _lastDisableWallQueue = disable;
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                if (kingdom == null) return;
                if (disable) DisableWallQueueing(kingdom);
                else RestoreWallQueueing(kingdom);
            }
            catch { }
        }

        /// <summary>取当前王国并复钳排队值（棋盘尚未创建时跳过）。</summary>
        private static void ClampWallQueueing()
        {
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                if (kingdom != null) DisableWallQueueing(kingdom);
            }
            catch { }
        }

        /// <summary>
        /// 禁用游戏自带的守墙排队机制：
        /// 1) 弓箭手：Kingdom.DistributeFreeArchers 按顺序给同侧弓箭手分配递增的 _guardDepth，
        ///    Archer.GetWallTargetPos 按"墙位 ∓（最小间距 + 深度×单位间距 + 随机偏移）"计算站位，
        ///    深度越大离墙越远；深度归零后统一站到墙后最近的可用距离（射程内）。
        /// 2) 骑士小队：Kingdom.RankKnights 给同侧骑士依次写入 rank = 1..N，
        ///    Knight.GetTargetPos 按"墙位 ∓ rank×_distanceFromWall"计算站位，
        ///    rank 归 1 后所有骑士（及其跟随的士兵）都站在墙后最近的一排。
        /// </summary>
        internal static void DisableWallQueueing(Kingdom kingdom)
        {
            if (!ModWindowBehaviour.DisableWallQueue) return;
            try
            {
                Il2CppSystem.Collections.Generic.ICollection<Archer> archers = kingdom.Archers;
                if (archers != null)
                {
                    int count = archers.Count;
                    if (count > 0)
                    {
                        if (_archerBuffer == null || _archerBuffer.Length < count)
                            _archerBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Archer>(Math.Max(count, 64));
                        var arr = _archerBuffer;
                        archers.CopyTo(arr, 0);
                        for (int i = 0; i < count; i++)
                        {
                            Archer a = arr[i];
                            if (a == null) continue;
                            try
                            {
                                // 只处理已分配守墙侧的弓箭手（未分配时 Side 为 0）
                                Side guardSide = a._guardSide;
                                if (guardSide != Side.Left && guardSide != Side.Right) continue;
                                int depth = a._guardDepth;
                                if (depth != 0)
                                {
                                    // 记录钳制前的真实排队深度（重新分配时覆盖为最新值），关闭开关后按此还原
                                    _savedGuardDepth[a.GetInstanceID()] = depth;
                                    a._guardDepth = 0;
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            try
            {
                Il2CppSystem.Collections.Generic.ICollection<Knight> knights = kingdom.Knights;
                if (knights == null) return;
                int count = knights.Count;
                if (count <= 0) return;
                if (_knightBuffer == null || _knightBuffer.Length < count)
                    _knightBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Knight>(Math.Max(count, 16));
                var arr = _knightBuffer;
                knights.CopyTo(arr, 0);
                for (int i = 0; i < count; i++)
                {
                    Knight k = arr[i];
                    if (k == null) continue;
                    try
                    {
                        // 正在去拿取铁剑（盔甲）的路上不要动 rank：目标站位由 rank 推导，
                        // 中途改 rank 会让状态机把它判成“该回城墙”，打断拿取动作
                        if (k._targetArmor != null) continue;
                        // rank 从 1 起算，1 = 墙后最近的一排；归 1 即取消骑士序列
                        int rank = k.rank;
                        if (rank > 1)
                        {
                            _savedKnightRank[k.GetInstanceID()] = rank;
                            k.rank = 1;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// 关闭“禁用排队机制”后立即恢复游戏原生排队：
        /// 把钳制前记录的原始 _guardDepth / rank 写回，弓箭手与骑士的目标位置由这两个值推导，
        /// 写回后它们会自行走回排队站位（无需重新触发游戏的分配流程）。
        /// </summary>
        internal static void RestoreWallQueueing(Kingdom kingdom)
        {
            try
            {
                Il2CppSystem.Collections.Generic.ICollection<Archer> archers = kingdom.Archers;
                if (archers != null)
                {
                    int count = archers.Count;
                    if (count > 0)
                    {
                        if (_archerBuffer == null || _archerBuffer.Length < count)
                            _archerBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Archer>(Math.Max(count, 64));
                        var arr = _archerBuffer;
                        archers.CopyTo(arr, 0);
                        for (int i = 0; i < count; i++)
                        {
                            Archer a = arr[i];
                            if (a == null) continue;
                            try
                            {
                                if (_savedGuardDepth.TryGetValue(a.GetInstanceID(), out int depth) && depth > 0)
                                    a._guardDepth = depth;
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
            _savedGuardDepth.Clear();

            try
            {
                Il2CppSystem.Collections.Generic.ICollection<Knight> knights = kingdom.Knights;
                if (knights != null)
                {
                    int count = knights.Count;
                    if (count > 0)
                    {
                        if (_knightBuffer == null || _knightBuffer.Length < count)
                            _knightBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Knight>(Math.Max(count, 16));
                        var arr = _knightBuffer;
                        knights.CopyTo(arr, 0);
                        for (int i = 0; i < count; i++)
                        {
                            Knight k = arr[i];
                            if (k == null) continue;
                            try
                            {
                                if (_savedKnightRank.TryGetValue(k.GetInstanceID(), out int rank) && rank > 1)
                                    k.rank = rank;
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
            _savedKnightRank.Clear();
        }

        /// <summary>收集真实墙体的水平边界（中心X与半宽），供弓箭手守墙bug修正使用。</summary>
        private void CollectWallBounds(Kingdom kingdom)
        {
            _wallBounds.Clear();
            try
            {
                Il2CppSystem.Collections.Generic.ICollection<Wall> walls = kingdom.Walls;
                if (walls == null) return;
                int count = walls.Count;
                if (count <= 0) return;
                if (_wallBuffer == null || _wallBuffer.Length < count)
                    _wallBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Wall>(Math.Max(count, 16));
                var arr = _wallBuffer;
                walls.CopyTo(arr, 0);
                for (int i = 0; i < count; i++)
                {
                    Wall wall = arr[i];
                    if (!TryGetX(wall, out float x)) continue;
                    // 墙体半宽：优先取碰撞体世界边界，失败用经验值 1（在建城墙通常还没有碰撞体，会走经验值）
                    float halfW = 1f;
                    try
                    {
                        Collider2D col = wall.GetComponentInChildren<Collider2D>();
                        if (col != null) halfW = Math.Max(0.3f, col.bounds.extents.x);
                    }
                    catch { }
                    // 施工中的城墙不能算作安全边界：刚付款的新墙/新塔会把最外层边界推到怪堆里，
                    // 让驻守的工人被判定成“墙内/安全”而跑出去干活
                    bool underConstruction = false;
                    try
                    {
                        WorkableBuilding building = wall._workableBuilding;
                        underConstruction = building != null && building.UnderConstruction;
                    }
                    catch { }
                    _wallBounds.Add(new WallBound { X = x, HalfW = halfW, UnderConstruction = underConstruction });
                }
            }
            catch { }
        }

        /// <summary>若 worldX 位于某墙体内部，计算修正位置（墙后城堡一侧，与墙保持 1 格间距）。</summary>
        private bool TryFixInsideWall(float worldX, float castleX, out float newX, out float wallX)
        {
            for (int i = 0; i < _wallBounds.Count; i++)
            {
                WallBound wb = _wallBounds[i];
                if (Math.Abs(worldX - wb.X) < wb.HalfW + 0.2f)
                {
                    wallX = wb.X;
                    float dir = castleX >= wb.X ? 1f : -1f;
                    newX = wb.X + dir * (wb.HalfW + 1f);
                    return true;
                }
            }
            newX = 0f;
            wallX = 0f;
            return false;
        }

        /// <summary>修正位于墙体内部的守卫槽（GuardSlot），弓箭手会自行走到修正后的位置。</summary>
        private void FixGuardSlotsInsideWalls(Kingdom kingdom, float castleX)
        {
            try
            {
                Il2CppSystem.Collections.Generic.HashSet<GuardSlot> slots = kingdom._guardSlots;
                if (slots == null || slots.Count <= 0) return;
                int slotCount = slots.Count;
                if (_guardSlotBuffer == null || _guardSlotBuffer.Length < slotCount)
                    _guardSlotBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<GuardSlot>(Math.Max(slotCount, 16));
                var slotArr = _guardSlotBuffer;
                slots.CopyTo(slotArr, 0);
                for (int i = 0; i < slotCount; i++)
                {
                    GuardSlot slot = slotArr[i];
                    if (slot == null) continue;
                    Vector3 pos;
                    try { pos = slot.transform.position; } catch { continue; }

                    if (TryFixInsideWall(pos.x, castleX, out float newX, out float wallX))
                    {
                        try
                        {
                            slot.transform.position = new Vector3(newX, pos.y, pos.z);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 单个单位此刻的避怪阈值：墙外 = 到该侧城墙外沿的距离 ×1.2，墙内 = 0；
        /// 该侧没有已完成城墙（未建/全被摧毁）时改按到主城的距离 ×1.2 计算，连主城都取不到才兜底回退游戏原生距离 InitialFleeRange(6)。
        /// 注意：本值只作为 ShouldFlee 强制补丁的“触发阈值”使用，不再写回 Worker/Archer.fleeRange 字段——
        /// 游戏在 Worker.Behaviour 里还用 fleeRange 计算驻守/避怪落点（border - Random.Range(fleeRange, fleeRange+3) × 侧向，
        /// 原版 6 意味着站在城墙内侧 6~9 格），把动态值（墙内=0、贴墙=很小）写进去会让落点贴到城墙上。
        /// </summary>
        internal static float ComputeFleeRange(Kingdom kingdom, float x)
        {
            try
            {
                Side side = AttackSideFromX(kingdom, x);
                if (HasCompletedWallOnSide(kingdom, side))
                {
                    if (IsInsideCompletedWalls(kingdom, x, side)) return 0f;   // 墙内：不需要避怪
                    if (TryGetOuterWallX(kingdom, side, out float outerX))
                        return Mathf.Abs(x - outerX) * FleeRangeWallFactor;
                }

                // 没有城墙可参照（刚开始还没建墙，或该侧城墙已被摧毁）：以主城为参照
                if (TryGetX(kingdom != null ? kingdom.castle : null, out float castleX))
                    return Mathf.Abs(x - castleX) * FleeRangeWallFactor;
            }
            catch { }
            return InitialFleeRange;
        }

        /// <summary>该侧是否有已完成城墙（施工中的不算；对侧的墙不算）。</summary>
        private static bool HasCompletedWallOnSide(Kingdom kingdom, Side side)
        {
            float castleX;
            bool hasCastle = TryGetX(kingdom != null ? kingdom.castle : null, out castleX);
            for (int i = 0; i < _wallBounds.Count; i++)
            {
                WallBound b = _wallBounds[i];
                if (b.UnderConstruction) continue;
                if (hasCastle)
                {
                    if (side == Side.Left && b.X > castleX) continue;
                    if (side == Side.Right && b.X < castleX) continue;
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// 扫描全场“危险敌人”并缓存其世界 X。单位自带的 Scanner 探测半径固定约 6（Awake 时固定，
        /// 前向范围是硬编码常量），fleeRange 写得再大，超出 6 的敌人也探测不到——这就是“避怪距离不对”的根因。
        /// 这里改用 EnemyManager.AllEnemies 自行扫描，ShouldFlee 强制补丁只查这份缓存（仅 X 轴距离，与原生一致）。
        /// 过滤口径与原生 ShouldFlee 一致：正在消失/已撤退/拖着战利品回巢/已死亡的不算威胁；
        /// 任务建筑与巨蛇弱点等特殊标签不触发避怪；GreedGolem 不是 Enemy（继承 Workable），天然不在列表里。
        /// </summary>
        private static void RefreshFleeEnemies()
        {
            _fleeEnemyX.Clear();
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                EnemyManager em = managers != null ? managers.enemies : null;
                if (kingdom == null || em == null) return;
                var all = em.AllEnemies;
                if (all == null || all.Count <= 0) return;

                if (_fleeEnemyBuffer == null || _fleeEnemyBuffer.Length < all.Count)
                    _fleeEnemyBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Enemy>(Math.Max(all.Count, 16));
                all.CopyTo(_fleeEnemyBuffer, 0);

                for (int i = 0; i < all.Count; i++)
                {
                    Enemy e = _fleeEnemyBuffer[i];
                    if (e == null) continue;
                    try
                    {
                        if (e.IsDespawning) continue;          // 正在消失（被打死/完成使命）
                        if (e.shouldRetreat) continue;         // 已决定撤回传送门
                        if (e.loot != null) continue;          // 已捡到金币/战利品回巢，不再构成威胁
                        Damageable d = e.damageable;
                        if (d != null)
                        {
                            if (d.isDead) continue;
                            // 死亡动画期间 isDead 可能还没置位：血量已空同样视为已死
                            if (d.hitPoints <= 0 && d.initialHitPoints > 0) continue;
                        }
                        // 任务建筑/巨蛇弱点被打上特殊标签，原生避怪不会对它们逃跑
                        if (e.CompareTag("QuestStructure") || e.CompareTag("SerpentWeakPoint")) continue;
                    }
                    catch { continue; }
                    if (!TryGetX(e, out float ex)) continue;
                    _fleeEnemyX.Add(ex);
                }
            }
            catch { }
        }

        /// <summary>
        /// ShouldFlee 强制补丁的共用判据：以 ComputeFleeRange 动态阈值（墙外=距墙×1.2，墙内=0）为准，
        /// 若任一危险敌人与本单位的 X 轴距离小于该阈值则强制避怪。原生 ShouldFlee 有“手上有活就返回 false”的闸门，
        /// 导致工人在开工状态即使敌人逼近也不避怪、先朝工作点走——状态机里 Flee(8) 优先于 Work(2)，
        /// 这里强制返回 true 后，状态转移回调会自动 ResetWorkState(true) 干净卸活，不存在手动卸活的恢复难题。
        /// 不再依赖写 fleeRange 字段：该字段同时参与驻守落点计算，写动态值会把驻守位置推到城墙边。
        /// </summary>
        internal static void ForceFleeIfThreatened(float fleeRange, float x, ref bool result)
        {
            if (result || fleeRange <= 0f || _fleeEnemyX.Count == 0) return;
            float best = float.MaxValue;
            for (int i = 0; i < _fleeEnemyX.Count; i++)
            {
                float d = Mathf.Abs(_fleeEnemyX[i] - x);
                if (d < best) best = d;
            }
            if (best < fleeRange) result = true;
        }

        /// <summary>
        /// 该单位所在侧是否还有“进攻怪物”（危险敌人缓存中与单位同侧的敌人）。
        /// 分界与 AttackSideFromX 一致（主城 x）；主城取不到时保守返回 true（有敌人就视为有威胁）。
        /// 用于避怪粘滞：一旦该侧触发过避怪，在进攻怪物清零前不让工人外出工作。
        /// </summary>
        internal static bool HasEnemyOnSide(Kingdom kingdom, float x)
        {
            if (_fleeEnemyX.Count == 0) return false;
            if (!TryGetX(kingdom != null ? kingdom.castle : null, out float divider)) return true;
            bool leftSide = x < divider;
            for (int i = 0; i < _fleeEnemyX.Count; i++)
            {
                bool enemyLeft = _fleeEnemyX[i] < divider;
                if (leftSide == enemyLeft) return true;
            }
            return false;
        }

        /// <summary>
        /// 修正位于墙体内部的弓箭手本体：仅处理守墙状态（ShouldGoToWall 或已在守卫槽）的弓箭手。
        /// 根因：游戏原生bug导致 _guardDepth 守卫深度漂移为负值且不再恢复，
        /// 目标站位 = 墙x ± (最小间距 + 深度×单位间距 + 随机偏移)，负深度会吃掉与墙间距，使弓箭手站进墙体内部。
        /// 治本方案：负深度钳制回 0，再把墙内弓箭手移出。
        /// </summary>
        private void FixArchersInsideWalls(Kingdom kingdom, float castleX)
        {
            try
            {
                Il2CppSystem.Collections.Generic.ICollection<Archer> archers = kingdom.Archers;
                if (archers == null) return;
                int count = archers.Count;
                if (count <= 0) return;
                if (_archerBuffer == null || _archerBuffer.Length < count)
                    _archerBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Archer>(Math.Max(count, 64));
                var arr = _archerBuffer;
                archers.CopyTo(arr, 0);
                for (int i = 0; i < count; i++)
                {
                    Archer a = arr[i];
                    if (a == null) continue;

                    // 只处理守墙状态的弓箭手
                    bool defending = false;
                    try { defending = a.ShouldGoToWall() || a.inGuardSlot; } catch { }
                    if (!defending) continue;

                    Vector3 pos;
                    try { pos = a.transform.position; } catch { continue; }

                    // 治本：守卫深度为负时钳制回 0，目标站位即恢复到墙后正常距离
                    try
                    {
                        int depth = a._guardDepth;
                        if (depth < 0) a._guardDepth = 0;
                    }
                    catch { }

                    // 治标：已站在墙体内的弓箭手立即移出到墙后
                    if (TryFixInsideWall(pos.x, castleX, out float newX, out float wallX))
                    {
                        try
                        {
                            a.transform.position = new Vector3(newX, pos.y, pos.z);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        internal static bool TryGetX<T>(T component, out float x) where T : Component
        {
            x = 0f;
            try { if (component == null) return false; x = component.transform.position.x; return true; }
            catch { return false; }
        }

        // ==================== 避怪阈值维护（城墙边界缓存 + ShouldFlee 强制补丁） ====================

        /// <summary>
        /// 刷新 ShouldFlee 强制补丁所需的城墙边界缓存（每 0.5 秒重建，建墙/破墙后尽快生效）。
        /// 不再往 Worker/Archer 的 fleeRange 字段写动态值：游戏还用该字段计算驻守/避怪落点
        /// （落点 = 城墙内侧 Random.Range(fleeRange, fleeRange+3)），写入动态值会导致工人驻守位置贴墙。
        /// 动态避怪阈值改由 ComputeFleeRange 在补丁内直接计算。
        /// </summary>
        private void RefreshWallBounds()
        {
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                if (kingdom == null) return;
                CollectWallBounds(kingdom);
            }
            catch { }
        }

        /// <summary>
        /// 传送门被摧毁后（某侧已无任何传送门）把长期驻守在该侧的工人改派到另一侧。
        /// 只改 Worker._targetSide——它只决定工人在 Shelter/待机时蹲哪一侧城墙（游戏在
        /// Worker.Behaviour 里用它算避难点）；工作分配、_currentWork/_queuedWork、队形、拾取目标一律不动，
        /// 因此工人仍可来该侧工作，改派跑向另一侧的过程中也照常接活。
        /// 判定依据是 Kingdom.closestPortal：传送门被摧毁时 Portal.Crumble 会 RemovePortal 并销毁对象，
        /// Kingdom 随即重算左右最近传送门，该侧变成 null。只有“恰好一侧没有传送门、另一侧还有”时才改派，
        /// 两侧都没有传送门时保持原样，避免工人来回改派。改派是事件驱动的：传送门被毁时全量处理一次，
        /// 之后新生成/读档恢复/被游戏改写所属侧的工人由 Worker.OnEnable、Worker.SetWorkerSide 补丁即时写死，
        /// 不再周期性扫描工人。
        /// </summary>
        private static void ReassignStationSideAfterPortalDestroyed()
        {
            try
            {
                Side? destroyedSide = GetMissingPortalSide();
                if (destroyedSide == null) return;
                Side targetSide = destroyedSide.Value == Side.Left ? Side.Right : Side.Left;

                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                if (kingdom == null) return;
                var workers = kingdom.Workers;
                if (workers == null || workers.Count <= 0) return;
                if (_stationWorkerBuffer == null || _stationWorkerBuffer.Length < workers.Count)
                    _stationWorkerBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Worker>(Math.Max(workers.Count, 16));
                workers.CopyTo(_stationWorkerBuffer, 0);

                int count = workers.Count;
                for (int i = 0; i < count; i++)
                {
                    Worker w = _stationWorkerBuffer[i];
                    if (w == null) continue;
                    try
                    {
                        if (w._targetSide != destroyedSide.Value) continue;
                        w._targetSide = targetSide;   // 仅切驻守侧，其它状态一概不碰
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// 工人启用（读档恢复/新生成）时立即按当前传送门状态写死驻守侧。
        /// 原生 OnEnable 会先按城堡位置初始化 _targetSide，这里紧接着纠正，避免工人先跑向旧驻守侧、
        /// 直到夜间重新决策才绕回来（周期检测只改值，无法打断已经开始的那段移动）。
        /// </summary>
        internal static void ApplyStationSideToWorker(Worker worker)
        {
            if (worker == null) return;
            try
            {
                Side? missingSide = GetMissingPortalSide();
                if (missingSide == null) return;
                if (worker._targetSide == missingSide.Value)
                    worker._targetSide = missingSide.Value == Side.Left ? Side.Right : Side.Left;
            }
            catch { }
        }

        /// <summary>恰好一侧没有传送门、另一侧还有时返回该缺失侧；两侧都有或都没有时返回 null。</summary>
        private static Side? GetMissingPortalSide()
        {
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                if (kingdom == null) return null;
                Sided<Portal> portals = kingdom.closestPortal;
                if (portals == null) return null;
                Portal left, right;
                try { left = portals.left; } catch { return null; }
                try { right = portals.right; } catch { return null; }
                if (left == null && right != null) return Side.Left;
                if (right == null && left != null) return Side.Right;
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 把“已分配但还没开始”的排队工作改派给离工作点最近的空闲工人。
        /// 原版 ActorTracker 按工人集合的固定顺序分配，先入队/先遍历的工人先接活，
        /// 经常出现远处的工人拿到工作、近处的空闲工人反而没活。
        /// 这里只动 _queuedWork（未开工），不打断已经在做的工作。
        /// </summary>
        private static void RebalanceQueuedWork()
        {
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                if (kingdom == null) return;
                var workers = kingdom.Workers;
                if (workers == null) return;
                int count = workers.Count;
                if (count <= 1) return;

                if (_balanceWorkerBuffer == null || _balanceWorkerBuffer.Length < count)
                    _balanceWorkerBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Worker>(Math.Max(count, 16));
                workers.CopyTo(_balanceWorkerBuffer, 0);

                // 先筛一次“可接活的空闲工人”池，供本轮所有排队工作共用。
                // 原实现在每个排队工作上重复检查全部工人的空闲状态，是 O(n²) 的互操作调用；
                // 筛成池后内层只剩浮点距离比较，1 秒一轮也不会形成帧尖峰。
                if (_balanceIdleBuffer == null || _balanceIdleBuffer.Length < count)
                    _balanceIdleBuffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Worker>(Math.Max(count, 16));
                if (_balanceIdleX == null || _balanceIdleX.Length < count)
                    _balanceIdleX = new float[count];

                int idleCount = 0;
                for (int j = 0; j < count; j++)
                {
                    Worker candidate = _balanceWorkerBuffer[j];
                    if (candidate == null) continue;
                    bool idle;
                    try { idle = candidate._currentWork == null && candidate._queuedWork == null; }
                    catch { idle = false; }
                    if (!idle) continue;                              // 只考虑完全空闲的工人
                    try { if (candidate.HasEmbarkableTarget()) continue; } catch { }
                    float candidateX;
                    if (!TryGetX(candidate, out candidateX)) continue;
                    _balanceIdleBuffer[idleCount] = candidate;
                    _balanceIdleX[idleCount] = candidateX;
                    idleCount++;
                }

                for (int i = 0; i < count && idleCount > 0; i++)
                {
                    Worker holder = _balanceWorkerBuffer[i];
                    if (holder == null) continue;

                    Workable job;
                    bool holderBusyWithOther;
                    try
                    {
                        job = holder._queuedWork;
                        holderBusyWithOther = holder._currentWork != null;
                    }
                    catch { continue; }
                    if (job == null || holderBusyWithOther) continue;   // 已在干活的不打断，改派只针对尚未开工的排队工作

                    float jobX, holderX;
                    if (!TryGetX(job, out jobX) || !TryGetX(holder, out holderX)) continue;
                    float holderDist = Mathf.Abs(jobX - holderX);

                    Worker best = null;
                    float bestDist = holderDist;
                    int bestSlot = -1;
                    for (int j = 0; j < idleCount; j++)
                    {
                        Worker candidate = _balanceIdleBuffer[j];
                        if (candidate == null) continue;                  // 本轮已被前面的工作改派走
                        float d = Mathf.Abs(jobX - _balanceIdleX[j]);
                        if (d < bestDist - WorkBalanceMargin)
                        {
                            bestDist = d;
                            best = candidate;
                            bestSlot = j;
                        }
                    }
                    if (best == null) continue;

                    // 走游戏自己的取消/接活接口，保证工作位占用记录同步
                    try { job.CancelWork(holder); }
                    catch { continue; }
                    try { if (holder._queuedWork == job) holder._queuedWork = null; } catch { }

                    bool available;
                    try { available = job.JobAvailable(); } catch { available = true; }
                    if (!available) continue;    // 工作位此刻不可用，留给游戏原生重分配

                    try { best.AssignJob(job); } catch { }
                    if (bestSlot >= 0) _balanceIdleBuffer[bestSlot] = null;   // 已接活，移出候选池
                }
            }
            catch { }
        }

        // ==================== 攻门（付费冲锋）小队优先级 ====================

        /// <summary>
        /// 骑士“高级程度”评分：先看 GameObject tag（侍从 "Squire" / 晋升骑士 "Knight"），
        /// 同级再看是否已拿到铁甲（NeedsArmor 为 false 表示已穿甲，含铁剑）。
        /// tag 权重 2 略高：游戏正是用 tag 区分侍从与骑士；
        /// 铁甲权重 1 作同级细分——两者都相同时才退回按距离比较。
        /// 不能用 _wasPromoted 判断：该字段只在 Knight.OnKnightPromote 里给“被替换下场的旧侍从”置位，
        /// 而 Knight.OnEnable 又会把它清零，活着的骑士此字段恒为 false。
        /// </summary>
        internal static int KnightAdvancedScore(Knight k)
        {
            if (k == null) return 0;
            int score = 0;
            try { if (k.CompareTag("Knight")) score += 2; } catch { }
            try { if (!k.NeedsArmor) score += 1; } catch { }
            return score;
        }

        /// <summary>是否为“高级骑士”（用铁剑晋升的骑士）：tag 为 Knight 或已穿铁甲。</summary>
        internal static bool IsAdvancedKnight(Knight k)
        {
            return KnightAdvancedScore(k) > 0;
        }

        /// <summary>
        /// 付费攻门时挑选出动小队：先比人数（满员优先），人数相同优先高级骑士，再比距离（更近优先）。
        /// 原版在 Kingdom.Knights 哈希集上遍历、只按弓箭手人数取最大值，人数相同则"后遍历者"胜出，
        /// 顺序随机且不看骑士等级。
        /// </summary>
        public static void PrepareChargePick(PayableBorder border)
        {
            _chargeBestId = -1;
            _chargeHasPick = false;
            _chargePickActive = true;
            _chargePicking = true;
            try
            {
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                if (kingdom == null || border == null) return;

                var knights = kingdom.Knights;
                if (knights == null || knights.Count <= 0) return;

                float borderX;
                if (!TryGetX(border, out borderX)) return;
                float range = border.knightRange;

                var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Knight>(knights.Count);
                knights.CopyTo(arr, 0);

                int bestCount = -1;
                int bestScore = -1;
                float bestDist = float.MaxValue;

                for (int i = 0; i < arr.Length; i++)
                {
                    Knight k = arr[i];
                    if (k == null) continue;
                    if (!k.CanCharge()) continue;   // 状态/队形/弓箭手/传送门条件
                    if (!TryGetX(k, out float kx)) continue;
                    float dist = Mathf.Abs(kx - borderX);
                    if (dist > range) continue;

                    int count;
                    try { count = k.numArchers; } catch { count = 0; }
                    if (count <= 0) continue;
                    int score = KnightAdvancedScore(k);

                    bool better;
                    if (count != bestCount) better = count > bestCount;
                    else if (score != bestScore) better = score > bestScore;
                    else better = dist < bestDist;
                    if (!better) continue;

                    bestCount = count;
                    bestScore = score;
                    bestDist = dist;
                    _chargeBestId = k.GetInstanceID();
                    _chargeHasPick = true;
                }
            }
            catch { }
            finally { _chargePicking = false; }
        }

        /// <summary>PayableBorder.Pay 结束：关闭过滤，后续付钱不受本次选人影响（可连续出动）。</summary>
        public static void FinishChargePick()
        {
            _chargePickActive = false;
        }

        /// <summary>
        /// 过滤是否有效：仅在 PayableBorder.Pay 执行期间生效。
        /// 不能延长到付钱之后——否则其余骑士的 CanCharge 被判 false，
        /// 游戏会认为该侧暂无可用小队而短暂禁止继续付钱，破坏原版的连续出动节奏。
        /// </summary>
        private static bool ChargeFilterActive()
        {
            if (_chargePicking) return false;
            if (!_chargePickActive) return false;
            return _chargeHasPick;   // 实例 ID 可能是负数，不能用 "_chargeBestId >= 0" 判定“是否有选中”
        }

        private static int SafeId(Knight k)
        {
            try { return k != null ? k.GetInstanceID() : -1; } catch { }
            return -1;
        }

        /// <summary>Pay 遍历骑士期间，只让选中的小队通过 CanCharge。</summary>
        public static bool AllowChargeCandidate(Knight k)
        {
            if (!ChargeFilterActive()) return true;
            return SafeId(k) == _chargeBestId;
        }

        /// <summary>
        /// 位置是否在该侧已完成的城墙内侧（以最外层已完成城墙为界；该侧没有城墙时以主城为界，等同墙外）。
        /// 施工中的城墙（_workableBuilding.UnderConstruction）不参与判断。
        /// </summary>
        private static bool IsInsideCompletedWalls(Kingdom kingdom, float x, Side side)
        {
            if (!TryGetOuterWallX(kingdom, side, out float outerX)) return false;
            return side == Side.Left ? x >= outerX : x <= outerX;
        }

        /// <summary>
        /// 按主城（城堡）位置把坐标分到左右侧——与怪物分侧口径完全一致。
        /// 取不到城堡位置时退回露营火堆位置兜底。
        /// </summary>
        private static Side AttackSideFromX(Kingdom kingdom, float x)
        {
            float divider;
            if (TryGetX(kingdom != null ? kingdom.castle : null, out divider))
                return x < divider ? Side.Left : Side.Right;
            return SideFromX(kingdom, x);
        }

        /// <summary>
        /// 取该侧城墙外沿 X：只统计城堡该侧（左侧 X &lt; 城堡X、右侧 X &gt; 城堡X）的墙，
        /// 左侧取最左墙的左边缘、右侧取最右墙的右边缘；该侧还没有城墙时退回城堡位置（主城旁），
        /// 避免拿另一侧的城墙当参照。
        /// </summary>
        private static bool TryGetOuterWallX(Kingdom kingdom, Side side, out float x)
        {
            x = 0f;
            float castleX;
            bool hasCastle = TryGetX(kingdom != null ? kingdom.castle : null, out castleX);
            int n = _wallBounds.Count;
            bool found = false;
            float best = 0f;
            for (int i = 0; i < n; i++)
            {
                WallBound b = _wallBounds[i];
                if (b.UnderConstruction) continue;   // 墙沿只按已完成城墙计算
                // 只统计城堡这一侧的墙：对侧的墙不能作为本侧的参照
                if (hasCastle)
                {
                    if (side == Side.Left && b.X > castleX) continue;
                    if (side == Side.Right && b.X < castleX) continue;
                }
                float edge = side == Side.Left ? b.X - b.HalfW : b.X + b.HalfW;
                if (!found || (side == Side.Left ? edge < best : edge > best))
                {
                    best = edge;
                    found = true;
                }
            }
            if (found)
            {
                x = best;
                return true;
            }
            // 该侧没有城墙（或尚未建成）时退回城堡位置：调用方需先用 HasCompletedWallOnSide 区分“无墙”的情况
            if (hasCastle)
            {
                x = castleX;
                return true;
            }
            return false;
        }

        /// <summary>按露营火堆位置判断世界坐标位于地图哪一侧。</summary>
        internal static Side SideFromX(Kingdom kingdom, float x)
        {
            float divider = 0f;
            try { divider = kingdom.CampfireWorldPosition; } catch { }
            return x < divider ? Side.Left : Side.Right;
        }

        /// <summary>与地图覆盖层一致的可运行状态：读档/开场/退出之外均执行（含菜单，此时仅溢出存入照常生效）。</summary>
        private static bool IsOverlayVisible()
        {
            try
            {
                Managers m = SingletonMonoBehaviour<Managers>.Inst;
                if (m == null) return false;
                Game g = m.game;
                if (g == null) return false;
                var s = g.state;
                return s != Game.State.Loading && s != Game.State.Intro && s != Game.State.Quitting;
            }
            catch { return false; }
        }

        private static bool IsMenuOpen()
        {
            try
            {
                Managers m = SingletonMonoBehaviour<Managers>.Inst;
                if (m == null) return false;
                Game g = m.game;
                return g != null && (g.state & Game.State.Menu) != 0;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// 摇铃后 NPC 立即上船：游戏给单位指派上船目标（SetEmbarkableTarget）是摇铃召唤的唯一途径，
    /// 此处若目标是船且单位为工人/弓箭手/骑士（摇铃只召唤这三类），把单位加入待登船队列，
    /// 由本组件的 Update 在下一帧执行 Embark，省去跑动。
    /// 玩家无这三类组件，天然不受影响；随从弓箭手由骑士上船时指派，同样被本补丁传送。
    /// 不能在这里直接 Embark：读档恢复乘客时游戏紧跟着自己也会调用 Embark，重复调用会重复占座，
    /// 下船时同一单位被处理两次，导致整船人卡在码头。延后一帧后若已登船就跳过。
    /// 开关关闭时补丁直接放行。
    /// </summary>
    [HarmonyPatch(typeof(Embarkee), nameof(Embarkee.SetEmbarkableTarget))]
    public static class EmbarkeeInstantBoardPatch
    {
        /// <summary>摇铃选人配额：超过所选人数的单位不指派上船目标（返回 false 跳过原生方法）。</summary>
        public static bool Prefix(Embarkee __instance, Embarkable embarkable)
        {
            return FeatureBehaviour.BellQuotaAllows(__instance, embarkable);
        }

        public static void Postfix(Embarkee __instance, Embarkable embarkable)
        {
            if (!ModWindowBehaviour.BellInstantBoard) return;
            if (__instance == null || embarkable == null) return;
            try
            {
                // 仅响应以"船"为目标的指派（排除舰队小艇等其他 Embarkable）
                if (embarkable._owner == null || embarkable._owner.TryCast<Boat>() == null) return;
                // 玩家与其他单位无这三类组件，不会被误传送
                if (__instance.GetComponent<Worker>() == null
                    && __instance.GetComponent<Archer>() == null
                    && __instance.GetComponent<Knight>() == null) return;
                if (__instance.IsEmbarked) return;
                FeatureBehaviour._pendingInstantBoards.Add(__instance);
            }
            catch { }
        }
    }

    /// <summary>游戏重新分配守墙弓箭手后立即取消排队，避免出现短暂的远距离站位。</summary>
    [HarmonyPatch(typeof(Kingdom), nameof(Kingdom.DistributeArchers))]
    public static class WallQueueArcherDistributionPatch
    {
        public static void Postfix(Kingdom __instance)
        {
            FeatureBehaviour.DisableWallQueueing(__instance);
        }
    }

    /// <summary>游戏重排骑士 rank 后立即取消骑士排队，保证骑士小队也站在墙后最近的一排。</summary>
    [HarmonyPatch(typeof(Kingdom), nameof(Kingdom.RankKnights))]
    public static class WallQueueKnightRankPatch
    {
        public static void Postfix(Kingdom __instance)
        {
            FeatureBehaviour.DisableWallQueueing(__instance);
        }
    }

    /// <summary>
    /// 游戏重新分配空闲弓箭手（守墙排队的主要路径，夜袭开始时触发）后立即取消排队。
    /// DistributeArchers 只处理部分情形，实际分配守墙深度的是 DistributeFreeArchers。
    /// </summary>
    [HarmonyPatch(typeof(Kingdom), nameof(Kingdom.DistributeFreeArchers))]
    public static class WallQueueFreeArcherDistributionPatch
    {
        public static void Postfix(Kingdom __instance)
        {
            FeatureBehaviour.DisableWallQueueing(__instance);
        }
    }

    /// <summary>
    /// 骑士正在去拿取铁剑（盔甲掉落 _targetArmor 非空）的路上时，禁止“回城墙驻守”的判定成立。
    /// “回城墙”的目标站位由 rank 推导，状态机在行走途中会反复评估 ShouldGoToWall，
    /// 一旦成立就会中断 GrabArmor 协程、把骑士拉回墙边（改 rank 时尤其明显）。
    /// 这里在拿取动作完成（_targetArmor 被清空）之前一律返回 false，保证拿取铁剑的过程不被打断；
    /// 拿到铁剑后恢复原生判定。DisableWallQueueing 也会跳过这类骑士，不动它们的 rank。
    /// </summary>
    [HarmonyPatch(typeof(Knight), nameof(Knight.ShouldGoToWall))]
    public static class KnightKeepArmorGrabPatch
    {
        public static bool Prefix(Knight __instance, ref bool __result)
        {
            try
            {
                if (__instance != null && __instance._targetArmor != null)
                {
                    __result = false;
                    return false;   // 跳过原生判定
                }
            }
            catch { }
            return true;
        }
    }

    /// <summary>
    /// 工人启用（读档恢复/新生成）后立即纠正驻守侧：原生 OnEnable 已先按城堡侧初始化，
    /// 只靠每 2 秒的周期检测会让工人在读档后先跑向旧驻守侧，直到夜间才回到改派侧。
    /// </summary>
    [HarmonyPatch(typeof(Worker), "OnEnable")]
    public static class WorkerStationSideOnEnablePatch
    {
        public static void Postfix(Worker __instance)
        {
            FeatureBehaviour.ApplyStationSideToWorker(__instance);
        }
    }

    /// <summary>
    /// 付费攻门前先挑出本次出动的骑士小队，供 CanCharge 过滤使用。
    /// 原版按哈希集遍历、只看弓箭手人数，顺序随机且不区分骑士等级。
    /// </summary>
    [HarmonyPatch(typeof(PayableBorder), nameof(PayableBorder.Pay))]
    public static class ChargeSquadPriorityPayPatch
    {
        public static void Prefix(PayableBorder __instance)
        {
            FeatureBehaviour.PrepareChargePick(__instance);
        }

        public static void Postfix()
        {
            FeatureBehaviour.FinishChargePick();
        }
    }

    /// <summary>
    /// PayableBorder.Pay 遍历骑士期间只放行选中的小队，
    /// 从而让原版循环选中"满员 → 高级骑士 → 更近"的那一支。
    /// </summary>
    [HarmonyPatch(typeof(Knight), nameof(Knight.CanCharge))]
    public static class ChargeSquadPriorityCanChargePatch
    {
        public static void Postfix(Knight __instance, ref bool __result)
        {
            if (!__result || __instance == null) return;
            if (!FeatureBehaviour.AllowChargeCandidate(__instance)) __result = false;
        }
    }

    /// <summary>
    /// 标记“本次摇铃付钱”流程：只有付钱触发的摇铃才弹选人窗口；
    /// 读档时 Boat.ApplyData 也会调用 OnBoatSummonBellRang 恢复乘客，那种情况必须直接放行。
    /// </summary>
    [HarmonyPatch(typeof(BoatSummoningBell), nameof(BoatSummoningBell.Pay))]
    public static class BoatSummoningBellPayPatch
    {
        public static void Prefix()
        {
            FeatureBehaviour._bellSummonFromPay = true;
        }

        public static void Postfix()
        {
            FeatureBehaviour._bellSummonFromPay = false;
        }
    }

    /// <summary>
    /// 摇铃后先弹出选人窗口，拦截本次原生召唤；玩家确认/取消后再由本模组主动调用原生方法。
    /// 这样船的 HasSummonedPassengers、各舰队船状态等副作用仍由原生逻辑完成。
    /// </summary>
    [HarmonyPatch(typeof(Boat), nameof(Boat.OnBoatSummonBellRang))]
    public static class BoatSummonBellSelectPatch
    {
        public static bool Prefix(Boat __instance)
        {
            if (FeatureBehaviour._bellAllowNativeSummon) return true;
            // 读取后立即消费标志：即使 Pay 内部抛异常导致 Postfix 未执行，也不会让标志悬挂到之后的
            // 读档恢复（Boat.ApplyData）调用上，避免读档时误弹选人窗口。
            bool fromPay = FeatureBehaviour._bellSummonFromPay;
            FeatureBehaviour._bellSummonFromPay = false;
            // 非付钱触发的调用（读档恢复乘客）不弹窗
            if (!fromPay) return true;
            if (__instance == null) return true;
            // 窗口显示成功则跳过原生召唤，等玩家确认后再调用
            return !FeatureBehaviour.BeginBellSelection(__instance);
        }
    }

    /// <summary>
    /// 工人避怪强制补丁：原生 ShouldFlee 开头有“手上有活（_currentWork/_queuedWork 非空）就返回 false”的闸门，
    /// 所以开工的工人即使敌人逼近也不会避怪，而是继续朝工作点走（表现为“避怪时先向工作地点跑”）。
    /// 另外工人自带的 Scanner 探测半径固定约 6（前向是硬编码常量），动态阈值再大也探测不到远处敌人（“避怪距离不对”）。
    /// 这里按模组缓存的危险敌人表 + ComputeFleeRange 动态阈值强制返回 true：状态机里 Flee(8) 优先于 Work(2)，
    /// 转移回调会自动 ResetWorkState(true) 卸活并转入 Flee，无需手动处理工作状态。
    /// 另外还有避怪粘滞：该侧有进攻怪物时一旦触发过避怪，就把工人记入 _stickyFleeWorkers，
    /// 此后怪物清零前 ShouldFlee 恒真——避免阈值边缘来回抖动导致的“避怪过程中间断性外出工作”。
    /// 粘滞只对城墙外的工人生效：回到城墙内后解除，退化为游戏原生驻守行为，避免墙内持续 Flee 跑向另一侧。
    /// 注意：不写回 fleeRange 字段——该字段还参与驻守/避怪落点计算（城墙内侧 Random.Range(fleeRange, fleeRange+3)），
    /// 写动态值会把驻守位置推到城墙边。
    /// </summary>
    [HarmonyPatch(typeof(Worker), nameof(Worker.ShouldFlee))]
    public static class WorkerForceFleePatch
    {
        public static void Postfix(Worker __instance, ref bool __result)
        {
            if (__instance == null) return;
            try
            {
                if (!FeatureBehaviour.TryGetX(__instance, out float x)) return;
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                if (kingdom == null) return;
                float fleeRange = FeatureBehaviour.ComputeFleeRange(kingdom, x);
                FeatureBehaviour.ForceFleeIfThreatened(fleeRange, x, ref __result);

                // 避怪粘滞：该侧有进攻怪物时，一旦触发过避怪（原生或强制），在怪物清零前持续避怪、
                // 不再外出工作（状态机 Flee 优先于 Work，ShouldFlee 恒真即一直保持 Flee 态）。
                // 但粘滞只对城墙外的工人生效：回到城墙内（fleeRange=0）后解除粘滞，退化为游戏原生
                // 驻守行为（原生墙内按 fleeRange=6 近距离避险），避免墙内持续 Flee 跑向另一侧。
                int id = __instance.GetInstanceID();
                if (FeatureBehaviour.HasEnemyOnSide(kingdom, x) && fleeRange > 0f)
                {
                    if (__result || FeatureBehaviour._stickyFleeWorkers.Contains(id))
                    {
                        __result = true;
                        FeatureBehaviour._stickyFleeWorkers.Add(id);
                    }
                }
                else
                {
                    FeatureBehaviour._stickyFleeWorkers.Remove(id);   // 怪物清零或已回到城墙内：解除粘滞，交还原生行为
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// 弓箭手避怪强制补丁：与 Worker 同理——原生 ShouldFlee 实时读 fleeRange，但 Scanner 探测半径固定
    /// （前向常量 6、后向 = 生成时的 shootRange），超出探测范围就看不见敌人。按模组缓存的危险敌人表 +
    /// ComputeFleeRange 动态阈值强制返回 true；fleeRange 字段保持原值（它还参与落点计算）。
    /// 避怪粘滞与工人一致：该侧有进攻怪物时一旦触发过避怪就记入 _stickyFleeArchers，怪物清零前
    /// ShouldFlee 恒真；粘滞只对城墙外弓箭手生效，回到城墙内后解除、退化为原生驻守行为。
    /// </summary>
    [HarmonyPatch(typeof(Archer), nameof(Archer.ShouldFlee))]
    public static class ArcherForceFleePatch
    {
        public static void Postfix(Archer __instance, ref bool __result)
        {
            if (__instance == null) return;
            try
            {
                if (!FeatureBehaviour.TryGetX(__instance, out float x)) return;
                Managers managers = SingletonMonoBehaviour<Managers>.Inst;
                Kingdom kingdom = managers != null ? managers.kingdom : null;
                if (kingdom == null) return;
                float fleeRange = FeatureBehaviour.ComputeFleeRange(kingdom, x);
                FeatureBehaviour.ForceFleeIfThreatened(fleeRange, x, ref __result);

                // 避怪粘滞（与工人相同）：粘滞只对城墙外的弓箭手生效，
                // 回到城墙内（fleeRange=0）后解除粘滞，交还原生驻守行为。
                int id = __instance.GetInstanceID();
                if (FeatureBehaviour.HasEnemyOnSide(kingdom, x) && fleeRange > 0f)
                {
                    if (__result || FeatureBehaviour._stickyFleeArchers.Contains(id))
                    {
                        __result = true;
                        FeatureBehaviour._stickyFleeArchers.Add(id);
                    }
                }
                else
                {
                    FeatureBehaviour._stickyFleeArchers.Remove(id);   // 怪物清零或已回到城墙内：解除粘滞，交还原生行为
                }
            }
            catch { }
        }
    }
}
