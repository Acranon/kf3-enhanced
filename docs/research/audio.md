# Audio

How King's Field III positions sounds, how libsnd manages the SPU's voices, and two
behaviours of the SPU that matter for an emulation of it. Addresses are `GAME.EXE`'s.
Library functions are named `lib_*` by behaviour (not by their PSY-Q names, which would
bind the runtime's emulation).

## Positioned sounds

`kf_Sound3D` (`0x80015410`)(se | `0x8000` flag, &pos, vol, maxDist, [sp+0x10] fade,
[sp+0x14] note) plays a sound from a place. It measures the distance to the listener:

- listener position `0x801B0A10` / `14` / `18` (3 x s32), listener yaw `0x801B0A24` (s16);
- d = `SquareRoot0` of the offsets >> 3, then << 3;
- nothing is played past maxDist;
- g = 128 * (fade - d) / fade, at most 127;
- base = g + (127 - g) / 4, or with the `0x8000` flag g + 32 + (127 - g) / 2, at most 127;
- angle = `func_80016AB8`(dx, dz) = -atan2(dx, dz) (4096 a turn), minus the yaw;
- one ear is reduced by base * |sin(angle)| * (127 - g) / 128: a near sound is centred, a
  far one panned hard. Front and back are folded together.

The levels are set once, at key-on: the sound does not follow the player as it plays.

`kf_PlaySound` (`0x80015790`)(se, left, right, note) keys the sound on through
`lib_SndKeyOn` (`0x8006DCE8`), with left and right at sp+0x14 / sp+0x18 and the voice
returned in v0; `kf_PlaySound` applies the effects volume after the law above.
`kf_PlaySound2D` (`0x8001576C`)(se, vol) plays a sound without a position.

### Area sounds

`kf_DrawObjects`' ambient zones (`0x8004104C`, `0x800410A8`; the call of `kf_PlaySound2D`
that returns to `0x80041284`) play the area's ambient sounds, such as the river. The
zone object is in s0: X at +0x11, Z at +0x19, half-size in units of 1024 at +0x35 / +0x36;
the box runs from X to X + 2048 * size.

### Sounds without a position

The player's own sounds go through `kf_PlaySound2D` (the calls returning to `0x8002ED50`,
`0x80030AA4`, `0x8002D940`). Monster AI (`func_80053C84`) also calls it.

## libsnd's voices

libsnd manages all 24 SPU voices (the u8 at `0x801B2684` is `0x18`). Its voice table is 24
records of `0x34` at `0x8009E9C4`: +0x1E the envelope level last read from the SPU (u16),
+0x33 busy (u8).

`lib_SndVoiceFlush` (`0x8006C720`) records each voice's envelope-zero bit in a 16-deep
history (16 x u32 at `0x8009EEC4`, write index at `0x8009EEBC`) and clears the busy byte
of voices that were zero throughout: a voice is freed once its envelope level has read
0 for 15 vblanks. Key-ons are flushed from the vblank interrupt (`lib_SndTick`,
`0x80071FF0`, which calls `lib_SndVoiceFlush` and then steps the sequences), so at the
SPU every key-on comes from there, whoever asked for it.

A fight with music playing peaked at 16 voices sounding, with no key-on onto a voice
that was still sounding.

### Envelope levels are mixer state

The SPU's per-voice current envelope level (register +0xC) is the mixer's state, not a
setting. Restoring it from a saved state gives voices that were off a stale non-zero
level that never decays; libsnd then never frees them, and with music on the remaining
voices, sound effects find none free (the weapon swing, sample `0x01000` at pitch `0x800`,
went silent in 9 of 10 loads). A state load must leave the levels alone and key every
voice off.

## ADPCM end flags

An ADPCM block's loop flags take effect after the block has played. For an end without
repeat, acting on them when the block is decoded mutes the voice before its last 28
samples, which are where a sound fades out: the sound is cut mid-wave (the secret-wall
door's sound went from an envelope of 32767 to 0 with the sample at -9186, an audible
pop). DuckStation acts on the flags at the end of the block. The loop-start flag sets
the repeat address when the block is decoded.
