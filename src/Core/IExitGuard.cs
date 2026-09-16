namespace CarroDesk.Core
{
    /// <summary>
    /// 退出守卫（规范 §3.5）。模块可选择性实现。
    /// RequestBlockExit 返回 true = 请求阻止退出并接管挑战流程；Host 对每个守卫设超时，超时按 false 放行。
    /// 挑战 UI 归 Host 持有，实现者只做"决策"。
    /// </summary>
    public interface IExitGuard
    {
        bool RequestBlockExit();
    }
}