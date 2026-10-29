# Fake-message regression checks

Run from the Fixes repository root:

```sh
dotnet run --project tests/FakeMessagesFix/FakeMessagesFix.Tests.csproj -c Release
```

This console harness instantiates the production service and invokes its registered
callbacks through SwiftlyS2 interface doubles. The tests check signon state changes,
chat interception before a rebroadcasting chat callback, exact command names,
hot load, slot replacement, configuration changes and disposal. They deliberately
reject pawn/controller access so dead and spectator players do not need a pawn
to chat once the connection is fully in game.

The dispatch fixture follows the framework's command-before-chat order. It does
not run CS2's native tokenizer or network stack. Verify on a live server that
pre-join `say` and `say_team` never appear, regular chat works after full signon,
and Workshop addon reconnects still complete.
