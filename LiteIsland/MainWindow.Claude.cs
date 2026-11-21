using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace LiteIsland;

public partial class MainWindow
{
    private ClaudeBridge? _claude;
    private readonly SemaphoreSlim _askGate = new(1, 1);

    /// <summary>Starts or stops the local server that lets Claude Code ask permission through the island.</summary>
    private void ApplyClaudeBridge()
    {
        try
        {
            if (_s.ClaudeBridge && !string.IsNullOrEmpty(_s.ClaudeToken))
            {
                if (_claude != null) return;
                var b = new ClaudeBridge(_s.ClaudeToken);
                b.OnPermission = AskClaude;
                b.OnDone = (project, msg) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    // never cover a question that is waiting for an answer
                    if (_askGate.CurrentCount == 0) return;
                    ClaudeAskWindow.Done(this, project, msg);
                }));
                b.Start();
                _claude = b;
            }
            else if (_claude != null)
            {
                _claude.Stop();
                _claude = null;
            }
        }
        catch { _claude = null; }
    }

    private async Task<int> AskClaude(string tool, string summary, string project)
    {
        // one question at a time; a second one waits its turn (and falls back to Claude's own prompt if it waits too long)
        if (!await _askGate.WaitAsync(TimeSpan.FromSeconds(100)).ConfigureAwait(false)) return 0;
        try
        {
            var t = await Dispatcher.InvokeAsync(() => ClaudeAskWindow.Ask(this, project, tool, summary, TimeSpan.FromSeconds(100)));
            return await t.ConfigureAwait(false);
        }
        finally { _askGate.Release(); }
    }
}
