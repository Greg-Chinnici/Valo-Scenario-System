# Replays

Anonymized `.vrp` replays, published here by the private converter's CI (a pull request per batch). The app's start
screen lists this folder.

**What a file contains:** a Valorant match as playback data: positions, view angles, health, weapons, utility, kills,
damage, the spike, scoreboard values, sampled 20 times a second. These are enriched and slimmed down versions of the VRF files you can download from Valorant. 
I have a separate (private) workflow for creating these files, as to not share any proprietary formats from riot. 

`vrpsim validate data/replays` (run by CI on every push) checks each file. Raw `.vrf` replays and keys are never
committed here (`.gitignore` refuses them).
