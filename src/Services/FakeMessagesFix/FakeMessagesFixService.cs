using Fixes.Models;
using Microsoft.Extensions.Options;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Misc;

namespace Fixes.Services.FakeMessagesFix;

internal class FakeMessagesFixService : IDisposable
{
    private readonly ISwiftlyCore _core;
    private readonly IDisposable? _configChangeSubscription;
    private Guid? _commandHookId;
    private Guid? _chatHookId;
    private bool _disposed;

    public FakeMessagesFixService(ISwiftlyCore core, IOptionsMonitor<Configuration> config)
    {
        _core = core;
        SetEnabled(config.CurrentValue.EnableFakeMessagesFix);
        _configChangeSubscription = config.OnChange(cfg => _core.Scheduler.NextTick(() =>
        {
            if (!_disposed)
            {
                SetEnabled(cfg.EnableFakeMessagesFix);
            }
        }));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _configChangeSubscription?.Dispose();
        SetEnabled(false);
    }

    private void SetEnabled(bool enabled)
    {
        var isEnabled = _commandHookId.HasValue;
        if (enabled == isEnabled)
        {
            return;
        }

        if (enabled)
        {
            EnableFakeMessagesFix();
            return;
        }

        DisableFakeMessagesFix();
    }

    private void EnableFakeMessagesFix()
    {
        // This stage precedes chat listeners and chat-command dispatch, including silent commands.
        _commandHookId = _core.Command.HookClientCommand(OnClientCommand);
        _chatHookId = _core.Command.HookClientChat(OnClientChat);
    }

    private void DisableFakeMessagesFix()
    {
        _core.Command.UnhookClientCommand(_commandHookId!.Value);
        _core.Command.UnhookClientChat(_chatHookId!.Value);
        _commandHookId = null;
        _chatHookId = null;
    }

    private HookResult OnClientCommand(int playerId, string commandLine)
    {
        return IsChatCommand(commandLine) ? CheckChatSender(playerId) : HookResult.Continue;
    }

    private HookResult OnClientChat(int playerId, string text, bool teamonly)
    {
        return CheckChatSender(playerId);
    }

    private HookResult CheckChatSender(int playerId)
    {
        if (playerId == -1)
        {
            return HookResult.Continue;
        }

        // PutInServer is earlier than FULL, and reconnects can regress signon without a new player object.
        // Check the current client state; pawn validity would also reject legitimate spectator chat.
        var player = _core.PlayerManager.GetPlayer(playerId);
        if (player is null)
        {
            return HookResult.Stop;
        }

        var client = player.ServerSideClient;
        return client.Address != 0 && client.IsInGame ? HookResult.Continue : HookResult.Stop;
    }

    private static bool IsChatCommand(string commandLine)
    {
        var command = commandLine.AsSpan();
        while (true)
        {
            var start = 0;
            while (start < command.Length && command[start] <= ' ')
            {
                start++;
            }
            command = command[start..];
            if (command.IsEmpty)
            {
                return false;
            }

            // The native tokenizer skips comments before the first token.
            if (command.StartsWith("//"))
            {
                var lineEnd = command.IndexOfAny('\r', '\n');
                if (lineEnd < 0)
                {
                    return false;
                }
                command = command[lineEnd..];
                continue;
            }
            if (command.StartsWith("/*"))
            {
                var commentEnd = command[2..].IndexOf("*/");
                if (commentEnd < 0)
                {
                    return false;
                }
                command = command[(commentEnd + 4)..];
                continue;
            }
            break;
        }

        if (command[0] == '"')
        {
            command = command[1..];
            var closingQuote = command.IndexOf('"');
            if (closingQuote >= 0)
            {
                command = command[..closingQuote];
            }
        }
        else
        {
            // Only inspect the first engine token, including adjacent quotes and token break characters.
            var length = 0;
            while (length < command.Length && command[length] > ' '
                && command[length] is not ('"' or '{' or '}' or '(' or ')' or '\'' or ':'))
            {
                length++;
            }
            command = command[..length];
        }

        return command.Equals("say", StringComparison.OrdinalIgnoreCase)
            || command.Equals("say_team", StringComparison.OrdinalIgnoreCase);
    }
}
