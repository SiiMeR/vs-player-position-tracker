# Player Position Tracker

This Vintage Story mod consists of 2 parts:
1) A server-side plugin that logs the absolute position of all players once per minute (configurable)
2) A client-side GUI / Map layer that allows to view this data directly on the map 

# Player Position Tracker 

Tracks the players positions once per minute and stores them in ModData for consumption by other sources. 

# Map Layer

This map layer is available to admins currently in Creative mode. The layer controls are a dropdown to select date (UTC) and a slider which allows you to move through the datapoints (60 * 24h = 1440 datapoints). For the currently made selection, a player pin is rendered for each datapoint in that bucket. 