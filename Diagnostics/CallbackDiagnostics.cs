namespace Polaris.Diagnostics
{
    /// <summary>订阅回调耗时过长时的告警；只在主线程写，无需加锁。</summary>
    internal static class CallbackDiagnostics
    {
        const double SlowWarnMillis = 8.0;

        internal static void RecordInvocation(string ownerGuid, string context, double millis)
        {
            if (millis >= SlowWarnMillis)
            {
                DiagnosticsRuntime.Logger.LogWarning(
                    $"[Polaris] Callback '{context}' (owner {ownerGuid}) took {millis:F1}ms this call.");
            }
        }

        // 异常计数目前没有任何读者（报告/诊断页都不消费），暂不重新引入统计表；
        // 接口仍保留此调用点，留给以后真正要用的时候再填。
        internal static void RecordException(string ownerGuid, string context)
        {
        }
    }
}
