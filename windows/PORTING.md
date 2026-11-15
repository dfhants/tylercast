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

These are in Swift code with no .NET counterpart yet. Each must be in place
when that counterpart is written.

From `f73a5f2` (security):

- [ ] **Session/Pipeline:** each session serves the stream on `StreamServer.RandomPath()`,
  with `allowedPeer` = the TV's IP and `bindHost` = `Ssdp.LocalAddressTowards(tv.Ip)`.
  The URL sent in `SetAVTransportURI` uses that path. (`StreamServer` supports all three already.)
- [ ] **Session polling:** after 3 state checks in a row that the TV doesn't answer, stop
  mirroring ("The TV stopped answering") rather than serve the screen to no one.
- [ ] **CLI `play` / tray app:** pick the TV with `Discovery.Choose` (exact IP, or a name only
  one renderer has; ambiguous choices are refused). Already in the core, with tests.
- [ ] **CLI `serve`:** print the random-path URL and say it is served to anyone on the network.

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
