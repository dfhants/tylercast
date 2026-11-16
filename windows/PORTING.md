# Keeping the Windows port in step with the Mac app

`windows/` is a .NET port of the Swift app in `Sources/`. The two share no code,
so every behaviour change in Swift has to be carried across by hand.

**Ported up to Swift commit:** `f73a5f2` ("Only the TV can watch the stream; trust discovery less").

To see what has changed in Swift since:

```bash
git diff f73a5f2 -- Sources Tests README.md
```

Port each change (with its tests) into `windows/`, then update the commit above.

## Swift behaviour not yet ported

Nothing from `f73a5f2`: all of it is in place (tests in `SecurityTests.cs` and `SessionTests.cs`):

- [x] Each session serves the stream on a random path, for the TV's IP only, bound to the
  interface facing the TV (`MirrorSession.StartAsync`).
- [x] After 3 state checks in a row that the TV doesn't answer, the session stops.
- [x] `play` picks the TV with `Discovery.Choose` (exact IP, or a name only one renderer has).
- [x] `serve` prints the random-path URL and says it is served to anyone on the network.

Not ported yet because the Windows equivalents don't exist yet (they are platform code, not
behaviour drift): screen capture, H.264 and AAC encoding, system-sound capture, the tray app,
keeping the display awake, stopping on sleep.

## .NET hardening not (yet) in Swift

Added while porting `f73a5f2`. Worth adding to the Swift app too:

- **No redirects** for the device description fetch or SOAP calls. Otherwise a device can
  pass `isOnDevice` with its LOCATION and then redirect the fetch to another host
  (URLSession follows redirects by default).
- **No DTDs** in device descriptions (refused outright), which rules out entity-expansion
  tricks. Check what `XMLParser` does with internal entities.
- **Response size limits:** 1 MB for descriptions and SOAP replies.
- **Windows only:** the stream port is bound with exclusive address use, so no other
  program can bind it alongside TylerCast.
