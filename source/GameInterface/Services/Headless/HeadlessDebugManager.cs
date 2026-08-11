using Common.Logging;
using Serilog;
using System;
using TaleWorlds.Library;

namespace GameInterface.Services.Headless
{
    /// <summary>
    /// Routes the engine's diagnostics to the log instead of the screen.
    /// </summary>
    /// <remarks>
    /// The engine reports serious problems by putting a message box on screen. In a process with no window
    /// that is not merely useless, it is fatal - the box cannot be shown or dismissed, and the server dies
    /// taking the reason with it. That is not hypothetical here: the one time a message box did get logged,
    /// the text was "Cannot load: Microsoft.Win32.Primitives.dll" and the process exited immediately after,
    /// with nothing in the managed log to say why.
    ///
    /// So every dialog becomes a log line, every render-debug call becomes nothing, and the server stays up
    /// and says what happened. Asserts are logged rather than thrown for the same reason: a failed assert
    /// deep in campaign loading should not end a session that other players are connected to.
    /// </remarks>
    internal sealed class HeadlessDebugManager : IDebugManager
    {
        private static readonly ILogger Logger = LogManager.GetLogger<HeadlessDebugManager>();

        public void ShowError(string message) => Logger.Error("[Engine] {Message}", message);

        public void ShowWarning(string message) => Logger.Warning("[Engine] {Message}", message);

        public void ShowMessageBox(string lpText, string lpCaption, uint uType)
            => Logger.Error("[Engine] {Caption}: {Text}", lpCaption, lpText);

        public void PrintError(string error, string stackTrace, ulong debugFilter)
            => Logger.Error("[Engine] {Error}{NewLine}{StackTrace}", error, Environment.NewLine, stackTrace);

        public void PrintWarning(string warning, ulong debugFilter)
            => Logger.Warning("[Engine] {Warning}", warning);

        public void Print(string message, int logLevel, Debug.DebugColor color, ulong debugFilter)
            => Logger.Information("[Engine] {Message}", message);

        public void Assert(bool condition, string message, string callerFile, string callerMethod, int callerLine)
        {
            if (condition) return;

            Logger.Error("[Engine] assert failed: {Message} ({File}:{Line} in {Method})",
                message, callerFile, callerLine, callerMethod);
        }

        public void SilentAssert(
            bool condition, string message, bool getDump, string callerFile, string callerMethod, int callerLine)
        {
            if (condition) return;

            Logger.Warning("[Engine] silent assert failed: {Message} ({File}:{Line} in {Method})",
                message, callerFile, callerLine, callerMethod);
        }

        public void DisplayDebugMessage(string message) => Logger.Debug("[Engine] {Message}", message);

        public void WriteDebugLineOnScreen(string message) => Logger.Debug("[Engine] {Message}", message);

        public void ReportMemoryBookmark(string message) => Logger.Debug("[Engine] {Message}", message);

        public void AbortGame() => Logger.Error("[Engine] AbortGame requested");

        public void DoDelayedexit(int returnCode) => Logger.Error("[Engine] delayed exit {Code}", returnCode);

        // Nothing below has any meaning without a screen to draw on.

        public void WatchVariable(string name, object value) { }

        public void RenderDebugLine(Vec3 position, Vec3 direction, uint color, bool depthCheck, float time) { }

        public void RenderDebugSphere(Vec3 position, float radius, uint color, bool depthCheck, float time) { }

        public void RenderDebugText3D(
            Vec3 position, string text, uint color, int screenPosOffsetX, int screenPosOffsetY, float time) { }

        public void RenderDebugFrame(MatrixFrame frame, float lineLength, float time) { }

        public void RenderDebugText(float screenX, float screenY, string text, uint color, float time) { }

        public void RenderDebugRectWithColor(float left, float bottom, float right, float top, uint color) { }

        public Vec3 GetDebugVector() => Vec3.Zero;

        public void SetDebugVector(Vec3 value) { }

        public void SetCrashReportCustomString(string customString) { }

        public void SetCrashReportCustomStack(string customStack) { }

        public void SetTestModeEnabled(bool testModeEnabled) { }
    }
}
