using System.Reflection;
using Fixes.Models;
using Microsoft.Extensions.Options;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.ProtobufDefinitions;
using SwiftlyS2.Shared.Scheduler;

namespace FakeMessagesFix.Tests;

internal static class Program
{
    public static int Main()
    {
        (string Name, Action Test)[] tests =
        [
            ("pre-join chat cannot reach an earlier rebroadcasting chat hook", BlockBeforeRebroadcast),
            ("only the current FULL signon state permits chat", SignonStates),
            ("exact chat command tokens include quoting and case variants", ChatCommandTokens),
            ("unrelated commands remain available before joining", UnrelatedCommands),
            ("a forced reconnect revokes chat immediately", ForcedReconnect),
            ("reused slots cannot inherit permission to chat", SlotReuse),
            ("fully joined clients work at hot load without a pawn", HotLoad),
            ("server console is allowed and missing players are blocked", ConsoleAndMissingPlayer),
            ("a missing native client blocks chat without reading signon memory", MissingNativeClient),
            ("configuration toggles register and remove both hooks once", ConfigurationLifecycle),
            ("queued configuration changes cannot revive a disposed service", PendingConfigurationAfterDispose),
        ];

        var failures = 0;
        foreach (var (name, test) in tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {exception.GetBaseException().Message}");
            }
        }

        Console.WriteLine($"{tests.Length - failures}/{tests.Length} checks passed.");
        return failures == 0 ? 0 : 1;
    }

    private static void BlockBeforeRebroadcast()
    {
        using var fixture = new Fixture();
        var broadcasts = 0;
        fixture.AddChatHook((_, _, _) =>
        {
            broadcasts++;
            return HookResult.Stop;
        });
        fixture.Start();

        foreach (var command in new[] { "say advertisement", "say_team advertisement", "say !command", "say /command" })
        {
            Equal(HookResult.Stop, fixture.DispatchChatCommand(1, command));
        }
        Equal(0, broadcasts);

        fixture.Client.SignonState = SignonState_t.SIGNONSTATE_FULL;
        fixture.DispatchChatCommand(1, "say hello");
        Equal(1, broadcasts);
    }

    private static void SignonStates()
    {
        foreach (var state in Enum.GetValues<SignonState_t>())
        {
            using var fixture = new Fixture(state);
            fixture.Start();
            var expected = state == SignonState_t.SIGNONSTATE_FULL ? HookResult.Continue : HookResult.Stop;
            Equal(expected, fixture.Command(1, "say hello"), state.ToString());
            Equal(expected, fixture.Command(1, "say_team hello"), state.ToString());
            Equal(expected, fixture.Chat(1, false), state.ToString());
            Equal(expected, fixture.Chat(1, true), state.ToString());
        }
    }

    private static void ChatCommandTokens()
    {
        using var fixture = new Fixture();
        fixture.Start();
        string[] commands =
        [
            "say hello", "say_team hello", "SAY hello", "SaY_TeAm hello", "say", "say_team",
            "\t say\thello", "\r\nSAY_TEAM\thello", "\"say\" hello", "\"SAY_TEAM\" hello",
            "say\"hello\"", "say_team\"hello\"", "\"say\"\"hello\"", "say:hello", "say_team(hello)",
            "/* comment */say hello", "// comment\nsay_team hello", " \t/* first */\n// second\r\n\"SaY\"\"hello\"",
        ];
        foreach (var command in commands)
        {
            Equal(HookResult.Stop, fixture.Command(1, command), command);
        }
    }

    private static void UnrelatedCommands()
    {
        using var fixture = new Fixture();
        fixture.Start();
        string[] commands =
        [
            "", " \t", "status", "jointeam 2", "joingame", "say_more hello", "say_team_extra hello",
            "sayfoo hello", "\"sayfoo\" hello", "sa\"y\" hello", "/* unfinished comment", "// comment without command",
        ];
        foreach (var command in commands)
        {
            Equal(HookResult.Continue, fixture.Command(1, command), command);
        }
    }

    private static void ForcedReconnect()
    {
        using var fixture = new Fixture(SignonState_t.SIGNONSTATE_FULL);
        fixture.Start();
        Equal(HookResult.Continue, fixture.Command(1, "say hello"));
        fixture.Client.SignonState = SignonState_t.SIGNONSTATE_CONNECTED;
        Equal(HookResult.Stop, fixture.Command(1, "say advertisement"));
        Equal(HookResult.Stop, fixture.Chat(1, false));
        fixture.Client.SignonState = SignonState_t.SIGNONSTATE_SPAWN;
        Equal(HookResult.Stop, fixture.Chat(1, false));
        fixture.Client.SignonState = SignonState_t.SIGNONSTATE_FULL;
        Equal(HookResult.Continue, fixture.Chat(1, false));
    }

    private static void SlotReuse()
    {
        using var fixture = new Fixture(SignonState_t.SIGNONSTATE_FULL);
        fixture.Start();
        Equal(HookResult.Continue, fixture.Chat(1, false));
        fixture.ReplacePlayer(SignonState_t.SIGNONSTATE_CONNECTED);
        Equal(HookResult.Stop, fixture.Command(1, "say advertisement"));
        Equal(HookResult.Stop, fixture.Chat(1, false));
        fixture.Client.SignonState = SignonState_t.SIGNONSTATE_FULL;
        Equal(HookResult.Continue, fixture.Chat(1, false));
    }

    private static void HotLoad()
    {
        using var fixture = new Fixture(SignonState_t.SIGNONSTATE_FULL);
        fixture.Start();
        // The player double throws if the service accesses pawn/controller/alive state.
        Equal(HookResult.Continue, fixture.Command(1, "say hello"));
        Equal(HookResult.Continue, fixture.Chat(1, false));
        Equal(HookResult.Continue, fixture.Chat(1, true));
    }

    private static void ConsoleAndMissingPlayer()
    {
        using var fixture = new Fixture();
        fixture.Start();
        Equal(HookResult.Continue, fixture.Command(-1, "say server message"));
        Equal(HookResult.Continue, fixture.Chat(-1, false));
        Equal(HookResult.Stop, fixture.Command(2, "say advertisement"));
        Equal(HookResult.Stop, fixture.Chat(2, true));
        Equal(HookResult.Continue, fixture.Command(2, "status"));
    }

    private static void ConfigurationLifecycle()
    {
        using var fixture = new Fixture();
        fixture.Config.Change(false);
        fixture.Start();
        Equal(0, fixture.CommandHookCount);
        Equal(0, fixture.ChatHookCount);
        fixture.Config.Change(true);
        fixture.Tick();
        Equal(1, fixture.CommandHookCount);
        Equal(1, fixture.ChatHookCount);
        fixture.Config.Change(true);
        fixture.Tick();
        Equal(1, fixture.CommandHookCount);
        Equal(1, fixture.ChatHookCount);
        fixture.Config.Change(false);
        fixture.Tick();
        Equal(0, fixture.CommandHookCount);
        Equal(0, fixture.ChatHookCount);
        Equal(HookResult.Continue, fixture.Command(1, "say hello"));
        fixture.Config.Change(true);
        fixture.Tick();
        Equal(HookResult.Stop, fixture.Command(1, "say advertisement"));
        fixture.Dispose();
        Equal(0, fixture.CommandHookCount);
        Equal(0, fixture.ChatHookCount);
        fixture.Dispose();
    }

    private static void MissingNativeClient()
    {
        using var fixture = new Fixture(SignonState_t.SIGNONSTATE_FULL);
        fixture.Client.Address = 0;
        fixture.Start();
        Equal(HookResult.Stop, fixture.Command(1, "say advertisement"));
        Equal(HookResult.Stop, fixture.Chat(1, false));
        Equal(0, fixture.Client.SignonStateReads);
    }

    private static void PendingConfigurationAfterDispose()
    {
        using var fixture = new Fixture();
        fixture.Start();
        fixture.Config.Change(true);
        fixture.Dispose();
        fixture.Tick();
        Equal(0, fixture.CommandHookCount);
        Equal(0, fixture.ChatHookCount);
        fixture.Config.Change(true);
        fixture.Tick();
        Equal(0, fixture.CommandHookCount);
        Equal(0, fixture.ChatHookCount);
    }

    private static void Equal<T>(T expected, T actual, string? context = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Dictionary<Guid, ICommandService.ClientCommandHandler> _commandHooks = [];
        private readonly Dictionary<Guid, ICommandService.ClientChatHandler> _chatHooks = [];
        private readonly Queue<Action> _nextTick = [];
        private readonly ISwiftlyCore _core;
        private IDisposable? _service;
        private IPlayer _player = null!;

        public TestOptionsMonitor Config { get; } = new();
        public TestServerSideClient Client { get; private set; } = null!;
        public int CommandHookCount => _commandHooks.Count;
        public int ChatHookCount => _chatHooks.Count;

        public Fixture(SignonState_t state = SignonState_t.SIGNONSTATE_CONNECTED)
        {
            ReplacePlayer(state);
            var commands = TestProxy.Create<ICommandService>((method, args) =>
            {
                switch (method.Name)
                {
                    case "HookClientCommand":
                        var commandId = Guid.NewGuid();
                        _commandHooks.Add(commandId, (ICommandService.ClientCommandHandler)args[0]!);
                        return commandId;
                    case "HookClientChat":
                        var chatId = Guid.NewGuid();
                        _chatHooks.Add(chatId, (ICommandService.ClientChatHandler)args[0]!);
                        return chatId;
                    case "UnhookClientCommand":
                        Equal(true, _commandHooks.Remove((Guid)args[0]!));
                        return null;
                    case "UnhookClientChat":
                        Equal(true, _chatHooks.Remove((Guid)args[0]!));
                        return null;
                    default:
                        throw new NotSupportedException(method.Name);
                }
            });
            var players = TestProxy.Create<IPlayerManagerService>((method, args) => method.Name switch
            {
                "GetPlayer" => (int)args[0]! == 1 ? _player : null,
                "GetAllPlayers" => new[] { _player },
                _ => throw new NotSupportedException(method.Name),
            });
            var scheduler = TestProxy.Create<ISchedulerService>((method, args) =>
            {
                if (method.Name != "NextTick")
                {
                    throw new NotSupportedException(method.Name);
                }
                _nextTick.Enqueue((Action)args[0]!);
                return null;
            });
            var events = TestProxy.Create<IEventSubscriber>((method, _) =>
                method.Name is "add_OnClientPutInServer" or "remove_OnClientPutInServer" ? null : throw new NotSupportedException(method.Name));
            _core = TestProxy.Create<ISwiftlyCore>((method, _) => method.Name switch
            {
                "get_Command" => commands,
                "get_PlayerManager" => players,
                "get_Scheduler" => scheduler,
                "get_Event" => events,
                _ => throw new NotSupportedException(method.Name),
            });
        }

        public void Start()
        {
            var serviceType = typeof(global::Fixes.Fixes).Assembly.GetType("Fixes.Services.FakeMessagesFix.FakeMessagesFixService", throwOnError: true)!;
            _service = (IDisposable)Activator.CreateInstance(serviceType, BindingFlags.Public | BindingFlags.Instance, null, [_core, Config], null)!;
        }

        public void ReplacePlayer(SignonState_t state)
        {
            Client = new TestServerSideClient(1) { SignonState = state };
            var client = Client;
            _player = TestProxy.Create<IPlayer>((method, _) => method.Name switch
            {
                "get_ServerSideClient" => client,
                _ => throw new NotSupportedException($"Readiness must not depend on {method.Name}"),
            });
        }

        public void AddChatHook(ICommandService.ClientChatHandler callback) => _chatHooks.Add(Guid.NewGuid(), callback);

        public HookResult Command(int slot, string command)
        {
            foreach (var callback in _commandHooks.Values)
            {
                var result = callback(slot, command);
                if (result != HookResult.Continue)
                {
                    return result;
                }
            }
            return HookResult.Continue;
        }

        public HookResult Chat(int slot, bool teamOnly)
        {
            foreach (var callback in _chatHooks.Values)
            {
                var result = callback(slot, "test message", teamOnly);
                if (result != HookResult.Continue)
                {
                    return result;
                }
            }
            return HookResult.Continue;
        }

        // SwiftlyS2 dispatches raw command hooks before chat callbacks; a prior callback
        // can rebroadcast and return Stop, so a chat-only guard cannot undo that send.
        public HookResult DispatchChatCommand(int slot, string command) =>
            Command(slot, command) == HookResult.Continue ? Chat(slot, false) : HookResult.Stop;

        public void Tick()
        {
            while (_nextTick.TryDequeue(out var callback))
            {
                callback();
            }
        }

        public void Dispose() => _service?.Dispose();
    }

    private sealed class TestOptionsMonitor : IOptionsMonitor<Configuration>
    {
        private Action<Configuration, string?>? _changed;
        public Configuration CurrentValue { get; private set; } = new();
        public Configuration Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<Configuration, string?> listener)
        {
            _changed += listener;
            return new Subscription(() => _changed -= listener);
        }

        public void Change(bool enabled)
        {
            CurrentValue = new Configuration { EnableFakeMessagesFix = enabled };
            _changed?.Invoke(CurrentValue, null);
        }

        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
