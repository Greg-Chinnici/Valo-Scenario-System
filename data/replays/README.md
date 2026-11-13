# Replays

Anonymized `.vrp` replays, published here by the private converter's CI (a pull request per batch). The app's start
screen lists this folder.

**What a file contains:** a Valorant match as playback data: positions, view angles, health, weapons, utility, kills,
damage, the spike, scoreboard values, sampled 20 times a second.

**What it doesn't:**
- Account ids and match ids are replaced by keyed pseudonyms. The same player has the same pseudonym, and the same
  generated name (like "Valiant Octopus 277"), in every file. Without the private key, a pseudonym can't be turned back
  into an account, and a match can't be looked up by id.
- No real names, no crosshair profile names, no recording time, no personal notes or tags.

Someone with access to match-history data may still be able to recognise a match from its map, agents and round
timeline. If you were in one of these matches and want it removed, open an issue with the file name.

`vrpsim validate data/replays` (run by CI on every push) checks each file. Raw `.vrf` replays and keys are never
committed here (`.gitignore` refuses them).
