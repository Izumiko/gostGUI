using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;

namespace gostGUI.Shared
{
    /// <summary>
    /// 把环境变量覆盖应用到子进程启动信息：正常的 psi.EnvironmentVariables 路径、net48 大小写重名父环境的
    /// 一次性规范化与临时环境兜底、以及 Windows 环境块的 32767 上限守卫。
    /// UI 进程与 runner 进程共用这一份实现；不引用 WinForms / 文件 IO。环境应用部分只回中文 error / 日志，
    /// 绝不抛异常；但 <see cref="ApplyAndStart"/> 会原样抛回 start 回调（子进程启动）的异常，供调用方识别 Win32 错误码。
    /// 需要真正启动子进程的调用方必须使用 <see cref="ApplyAndStart"/>：Process.Start 会从活环境捕获子进程环境块
    /// （即使没有任何覆盖项），这次捕获必须与别的调用的临时覆盖窗口同处一把锁内。
    /// 传入的 psi 必须是新建且尚未访问过 EnvironmentVariables 的实例；若调用前已访问且首次访问失败，
    /// net48 会留下半初始化的环境字典，本类无法再据此重建完整父环境。
    /// </summary>
    public static class ChildEnvironment
    {
        /// <summary>Windows 环境块硬上限（含结尾的两个 '\0'）。</summary>
        private const int MaxBlockSize = 32767;

        /// <summary>
        /// 父环境快照、重名探测/规范化、写入 psi、临时环境兜底与还原、以及子进程启动时的环境捕获
        /// 全部串行化在这把锁内：并发的启动既不会快照到另一个调用临时写进本进程环境的覆盖值，
        /// 也不会在另一个调用的“临时覆盖 → 还原”窗口里启动子进程。
        /// </summary>
        private static readonly object EnvironmentLock = new object();

        /// <summary>大小写重名父环境只在第一次需要覆盖时尝试规范化一次（只在 EnvironmentLock 内读写）。</summary>
        private static bool _processEnvironmentNormalized;

        /// <summary>
        /// 复制一份父环境快照（net48 的 IDictionary 不实现泛型枚举，只能逐项复制）。
        /// 与 ApplyAndStart 共用同一把静态锁：取快照时不可能看到另一个线程临时写进本进程环境的覆盖值。
        /// LaunchPipeline.Build 只消费这份快照（COMSPEC 也改从快照派生的有效环境取），Build 本身不再读活环境，
        /// 因此快照就是本次启动唯一的环境来源。
        /// </summary>
        public static List<KeyValuePair<string, string>> SnapshotParentEnvironment()
        {
            lock (EnvironmentLock)
            {
                return SnapshotEnvironment();
            }
        }

        /// <summary>
        /// 把覆盖项写入子进程的启动信息。失败信息通过 logSystem（中文）报告，绝不抛异常。
        /// 任何非空 error 表示必须放弃启动；没有覆盖项时是纯 no-op（连 psi.EnvironmentVariables 都不访问）。
        /// 本方法不启动进程；若随后要启动子进程，请改用 <see cref="ApplyAndStart"/>，否则 Process.Start 的
        /// 环境捕获会落在锁外，可能看到别的调用临时写进本进程环境的覆盖值。
        /// </summary>
        public static void Apply(
            ProcessStartInfo psi,
            IEnumerable<KeyValuePair<string, string>> overrides,
            Action<string> logSystem,
            out string error)
        {
            ApplyAndStart(psi, overrides, logSystem, null, out error);
        }

        /// <summary>
        /// 内部入口：在 EnvironmentLock 内完成环境应用，并紧接着执行 start（若提供）。
        /// Process.Start 即使在零覆盖项时也会从活环境播种子进程环境块，所以“捕获 + 启动”必须在锁内完成，
        /// 否则并发的临时覆盖窗口会污染本次子进程。环境部分自身的异常只转成中文 error；
        /// start 抛出的异常（如 Win32Exception 740）原样抛给调用方，锁与临时环境仍会按正常路径释放/还原。
        /// 返回值表示 start 是否被调用并返回 true；start 为 null 时返回 false。
        /// </summary>
        internal static bool ApplyAndStart(
            ProcessStartInfo psi,
            IEnumerable<KeyValuePair<string, string>> overrides,
            Action<string> logSystem,
            Func<bool> start,
            out string error)
        {
            error = "";
            var logs = new List<string>();
            bool started = false;

            try
            {
                List<KeyValuePair<string, string>> pairs;
                try
                {
                    pairs = MaterializeOverrides(overrides);
                }
                catch (Exception ex)
                {
                    error = "应用环境变量失败：" + ex.Message;
                    return false;
                }

                lock (EnvironmentLock)
                {
                    try
                    {
                        ApplyEnvironment(psi, pairs, logs, out error);
                    }
                    catch (Exception ex)
                    {
                        // 兜底：环境应用部分只回 error / 中文日志，不抛异常。
                        error = "应用环境变量失败：" + ex.Message;
                    }

                    // 注意：start 的异常不能被上面的 catch 吞掉——ProcessManager 要靠 Win32Exception 识别 740。
                    // 环境部分此时已经完成或还原，锁会在异常路径上照常释放。
                    if (string.IsNullOrEmpty(error) && start != null)
                    {
                        started = start();
                    }
                }
            }
            finally
            {
                // 日志在环境锁外送出：OutputReceived 可能是 UI 回调，不能在锁内调用。
                FlushLogs(logs, logSystem);
            }

            return started;
        }

        /// <summary>
        /// 锁内的环境应用主体：32767 守卫 → 一次性规范化 → 写 psi → 必要时临时环境兜底。
        /// 没有覆盖项时什么都不做（尤其不访问 psi.EnvironmentVariables，懒播种在重名父环境下会抛异常）。
        /// 调用方必须持有 EnvironmentLock。
        /// </summary>
        private static void ApplyEnvironment(ProcessStartInfo psi, List<KeyValuePair<string, string>> pairs,
            List<string> logs, out string error)
        {
            error = "";
            if (pairs.Count == 0)
            {
                return;
            }

            if (psi == null)
            {
                error = "子进程启动信息为空，无法应用环境变量。";
                return;
            }

            // R12：真正交给 CreateProcess 的环境块还带两个结尾 '\0'，所以按 sum + 2 守卫；
            // 超限时在碰任何东西之前就返回：不规范化、不写 psi、更不临时改进程环境。
            string sizeError = CheckBlockSize(pairs);
            if (!string.IsNullOrEmpty(sizeError))
            {
                error = sizeError;
                return;
            }

            NormalizeProcessEnvironmentIfNeeded(logs);
            if (!TryWriteToStartInfo(psi, pairs))
            {
                ApplyThroughTemporaryProcessEnvironment(psi, pairs, logs, out error);
            }
        }

        /// <summary>把调用方的覆盖序列复制成稳定列表，同时跳过无法写进环境块的空键。</summary>
        private static List<KeyValuePair<string, string>> MaterializeOverrides(
            IEnumerable<KeyValuePair<string, string>> overrides)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            if (overrides == null)
            {
                return pairs;
            }

            foreach (KeyValuePair<string, string> pair in overrides)
            {
                if (string.IsNullOrEmpty(pair.Key))
                {
                    continue;
                }

                pairs.Add(new KeyValuePair<string, string>(pair.Key, pair.Value ?? ""));
            }

            return pairs;
        }

        /// <summary>
        /// 按 Windows 环境块口径计算“父环境 + 覆盖项”的最终大小：每项 key + '=' + value + '\0'，再加结尾两个 '\0'。
        /// 超过 32767 时返回中文错误，调用方必须据此放弃启动。
        /// </summary>
        private static string CheckBlockSize(List<KeyValuePair<string, string>> overrides)
        {
            var effective = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                string key = Convert.ToString(entry.Key);
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                effective[key] = Convert.ToString(entry.Value) ?? "";
            }

            // 覆盖项按枚举顺序生效；同名（大小写不敏感）只留最后一项，与真正写入 psi 的结果一致。
            foreach (KeyValuePair<string, string> pair in overrides)
            {
                effective.Remove(pair.Key);
                effective[pair.Key] = pair.Value ?? "";
            }

            long total = 2; // 环境块结尾的两个 '\0'
            foreach (KeyValuePair<string, string> pair in effective)
            {
                total += pair.Key.Length + 1 + pair.Value.Length + 1;
            }

            if (total > MaxBlockSize)
            {
                return "环境变量块过大：合并后共 " + total + " 字节（含结尾 2 字节），超过 Windows 上限 "
                    + MaxBlockSize + " 字节，请减少环境变量。";
            }

            return "";
        }

        /// <summary>
        /// 父环境块里若同一变量名出现大小写两份（某些 shell / Node 启动链会造出来，如 HTTP_PROXY 与 http_proxy），
        /// net48 的 ProcessStartInfo.EnvironmentVariables 首次访问会因内部播种重复键抛 ArgumentException。
        /// 这里在第一次需要写覆盖项时把本进程环境一次性规范化：每个重名组只留一个拼写，并记一条系统日志。
        /// 调用方必须持有 EnvironmentLock。
        /// </summary>
        private static void NormalizeProcessEnvironmentIfNeeded(List<string> logs)
        {
            if (_processEnvironmentNormalized || !ParentEnvironmentHasDuplicateKeys())
            {
                return;
            }

            _processEnvironmentNormalized = true;
            List<string> normalized = NormalizeDuplicateProcessEnvironmentKeys();
            logs.Add(normalized.Count > 0
                ? "检测到父进程存在大小写重名的环境变量，已自动规范化：" +
                  string.Join("、", normalized.ToArray())
                : "检测到父进程存在大小写重名的环境变量，规范化未完成，已改用临时环境启动。");
        }

        /// <summary>父环境块里是否存在仅大小写不同的重名变量。</summary>
        private static bool ParentEnvironmentHasDuplicateKeys()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                string key = Convert.ToString(entry.Key);
                if (key.Length == 0)
                {
                    continue;
                }

                if (!seen.Add(key))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 规范化本进程环境：每个大小写不敏感的重名组保留首次出现的拼写与值，其余拼写逐个删除。
        /// SetEnvironmentVariable 的按名删除/写入都忽略大小写，所以“删一个拼写、再补写保留值”
        /// 每轮至少减少一个条目；返回值是已成功规范化的变量名，供调用方记系统日志。
        /// </summary>
        private static List<string> NormalizeDuplicateProcessEnvironmentKeys()
        {
            var order = new List<string>();
            var groups = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                string key = Convert.ToString(entry.Key);
                if (key.Length == 0)
                {
                    continue;
                }

                List<KeyValuePair<string, string>> group;
                if (!groups.TryGetValue(key, out group))
                {
                    group = new List<KeyValuePair<string, string>>();
                    groups[key] = group;
                    order.Add(key);
                }

                group.Add(new KeyValuePair<string, string>(key, Convert.ToString(entry.Value)));
            }

            var normalized = new List<string>();
            foreach (string groupKey in order)
            {
                List<KeyValuePair<string, string>> group = groups[groupKey];
                if (group.Count < 2)
                {
                    continue;
                }

                string keepSpelling = group[0].Key;
                string keepValue = group[0].Value;
                int before = CountEnvironmentVariants(keepSpelling);

                // 正常情况下一次删一个拼写、补写保留值就能收敛；guard 只防住极端环境下的死循环。
                int guard = 0;
                while (CountEnvironmentVariants(keepSpelling) > 1 && guard++ < 8)
                {
                    // 每轮重新取一个当前存在的拼写：删除按名字忽略大小写，删掉一条后再补写保留值。
                    string duplicateSpelling = FirstEnvironmentSpelling(keepSpelling);
                    if (duplicateSpelling == null)
                    {
                        break;
                    }

                    try
                    {
                        // 先补写保留值，再删一个拼写，最后再补写一次：
                        // 即便删除命中的是保留拼写，变量也不会在这一轮里消失，最终值仍是 keepValue。
                        Environment.SetEnvironmentVariable(keepSpelling, keepValue);
                        Environment.SetEnvironmentVariable(duplicateSpelling, null);
                        Environment.SetEnvironmentVariable(keepSpelling, keepValue);
                    }
                    catch (Exception)
                    {
                        // 规范化失败就停手：后续会退回临时覆盖兜底。
                        break;
                    }
                }

                if (CountEnvironmentVariants(keepSpelling) < before)
                {
                    normalized.Add(keepSpelling);
                }
            }

            return normalized;
        }

        private static string FirstEnvironmentSpelling(string key)
        {
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                string name = Convert.ToString(entry.Key);
                if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }

            return null;
        }

        private static int CountEnvironmentVariants(string key)
        {
            int count = 0;
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                string name = Convert.ToString(entry.Key);
                if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// 把覆盖项写进 psi.EnvironmentVariables；病态父环境下首次访问会抛 ArgumentException。
        /// psi 的字典大小写不敏感，写入一种拼写就会覆盖同名变量的所有现有拼写，覆盖项因此始终生效。
        /// </summary>
        private static bool TryWriteToStartInfo(ProcessStartInfo psi, List<KeyValuePair<string, string>> overrides)
        {
            try
            {
                foreach (KeyValuePair<string, string> pair in overrides)
                {
                    psi.EnvironmentVariables[pair.Key] = pair.Value ?? "";
                }

                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// 最后兜底：psi.EnvironmentVariables 首次访问失败后，net48 只留下半初始化的字典（父环境会缺项）。
        /// 这里临时把覆盖项写进本进程环境，用临时环境里的完整内容整体重建 psi 的字典，再在 finally 里还原；
        /// 子进程启动时从 psi 字典构造环境块，因此仍能拿到“完整父环境 + 覆盖项”，本进程环境不残留。
        /// 调用方必须持有 EnvironmentLock。
        /// </summary>
        private static void ApplyThroughTemporaryProcessEnvironment(ProcessStartInfo psi,
            List<KeyValuePair<string, string>> overrides, List<string> logs, out string error)
        {
            error = "";
            var saved = new List<KeyValuePair<string, string>>();
            try
            {
                // 注意：apply 必须在 try 内——任何一步写失败都要由 finally 把已写入的项还原。
                ApplyOverridesToProcessEnvironment(overrides, saved);
                RebuildStartInfoEnvironment(psi);
            }
            catch (Exception ex)
            {
                error = "应用环境变量失败：" + ex.Message;
            }
            finally
            {
                List<string> failed = RestoreProcessEnvironment(saved);
                if (failed.Count > 0)
                {
                    // 子进程不受影响，但父进程环境已被永久改写：这一条必须让用户看见。
                    logs.Add("环境变量还原失败，本进程环境已被修改：" + string.Join("、", failed.ToArray()));
                }
            }
        }

        /// <summary>
        /// 把覆盖项写进本进程环境，并把每一项的原值（null 表示原本不存在）追加进 saved。
        /// 覆盖键若有大小写不同的现有拼写，所有拼写都写成同一个覆盖值，避免子进程留下旧值的孪生项；
        /// 原值在写入前采集，apply 中途失败时 saved 里仍是可还原的旧值。
        /// </summary>
        private static void ApplyOverridesToProcessEnvironment(List<KeyValuePair<string, string>> overrides,
            List<KeyValuePair<string, string>> saved)
        {
            foreach (KeyValuePair<string, string> pair in overrides)
            {
                List<string> spellings = FindEnvironmentKeySpellings(pair.Key);
                if (!spellings.Exists(s => string.Equals(s, pair.Key, StringComparison.Ordinal)))
                {
                    spellings.Add(pair.Key);
                }

                foreach (string spelling in spellings)
                {
                    saved.Add(new KeyValuePair<string, string>(
                        spelling, Environment.GetEnvironmentVariable(spelling)));
                }

                foreach (string spelling in spellings)
                {
                    Environment.SetEnvironmentVariable(spelling, pair.Value ?? "");
                }
            }
        }

        private static List<string> FindEnvironmentKeySpellings(string key)
        {
            var spellings = new List<string>();
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                string name = Convert.ToString(entry.Key);
                if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase) && !spellings.Contains(name))
                {
                    spellings.Add(name);
                }
            }

            return spellings;
        }

        /// <summary>逐项还原临时覆盖过的环境变量，返回还原失败的键名（调用方必须把失败写成系统日志）。</summary>
        private static List<string> RestoreProcessEnvironment(List<KeyValuePair<string, string>> saved)
        {
            var failed = new List<string>();
            if (saved == null)
            {
                return failed;
            }

            // 逆序还原，逐项恢复覆盖前的原值（null 表示删除）。
            for (int i = saved.Count - 1; i >= 0; i--)
            {
                try
                {
                    Environment.SetEnvironmentVariable(saved[i].Key, saved[i].Value);
                }
                catch (Exception)
                {
                    if (!failed.Contains(saved[i].Key))
                    {
                        failed.Add(saved[i].Key);
                    }
                }
            }

            return failed;
        }

        /// <summary>
        /// 用当前（已临时写入覆盖项的）本进程环境整体重建 psi 的环境字典。
        /// net48 首次访问失败后字段已是半初始化的 StringDictionary，再次访问不会重新播种，
        /// 所以必须先 Clear 再按大小写不敏感去重后逐项写入，父环境才不会缺项。
        /// </summary>
        private static void RebuildStartInfoEnvironment(ProcessStartInfo psi)
        {
            var target = psi.EnvironmentVariables; // 第二次访问不会再抛：首次失败时字段已经被赋成半初始化的字典
            target.Clear();

            var effective = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                string key = Convert.ToString(entry.Key);
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                effective[key] = Convert.ToString(entry.Value) ?? "";
            }

            foreach (KeyValuePair<string, string> pair in effective)
            {
                target[pair.Key] = pair.Value;
            }
        }

        /// <summary>复制父环境；调用方必须持有 EnvironmentLock。</summary>
        private static List<KeyValuePair<string, string>> SnapshotEnvironment()
        {
            var result = new List<KeyValuePair<string, string>>();
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                result.Add(new KeyValuePair<string, string>(
                    Convert.ToString(entry.Key),
                    Convert.ToString(entry.Value)));
            }

            return result;
        }

        /// <summary>
        /// 在环境锁外把收集到的中文系统消息交给调用方；日志回调自身的异常必须吞掉，
        /// 否则“Apply 绝不抛异常”和 ProcessManager 的“启动失败也返回 bool”都会被打破。
        /// </summary>
        private static void FlushLogs(List<string> logs, Action<string> logSystem)
        {
            if (logs.Count == 0 || logSystem == null)
            {
                return;
            }

            foreach (string line in logs)
            {
                try
                {
                    logSystem(line);
                }
                catch
                {
                    // 日志回调故障不能影响环境应用结论。
                }
            }
        }
    }
}
