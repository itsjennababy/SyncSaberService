﻿using System;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;

namespace SyncSaberService.Data
{
    class ScrappedSong
    {
        private readonly Regex _digitRegex = new Regex("^[0-9]+$", RegexOptions.Compiled);
        private readonly Regex _beatSaverRegex = new Regex("^[0-9]+-[0-9]+$", RegexOptions.Compiled);

        [JsonProperty("key")]
        public string key;
        [JsonProperty("songName")]
        public string songName;
        [JsonProperty("songSubName")]
        public string songSubName;
        [JsonProperty("authorName")]
        public string authorName;
        [JsonProperty("bpm")]
        public float bpm;
        [JsonProperty("Diffs")]
        public List<ScrappedDifficulties> difficulties;
        [JsonProperty("playedCount")]
        public int playedCount;
        [JsonProperty("upVotes")]
        public int upVotes;
        [JsonProperty("downVotes")]
        public int downVotes;
        [JsonProperty("hash")]
        public string hashMd5;

        public override string ToString()
        {
            StringBuilder retStr = new StringBuilder();
            retStr.Append("SongInfo:");
            retStr.AppendLine("   Index: " + key);
            retStr.AppendLine("   Name: " + songName);
            retStr.AppendLine("   Author: " + authorName);
            return retStr.ToString();
        }
    }

    public class ScrappedDifficulties
    {
        [JsonProperty("Diff")]
        public string difficulty;
        [JsonProperty("scores")]
        public int scores;
        [JsonProperty("Stars")]
        public float stars;

    }

}