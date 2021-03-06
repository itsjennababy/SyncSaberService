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
using SyncSaberLib.Web;


namespace SyncSaberLib.Data
{
    /// <summary>
    /// Holds all the data for a song provided by Beat Saver's API.
    /// </summary>
    public class BeatSaverSong : IEquatable<BeatSaverSong>
    {
        private const string SONG_DETAILS_URL_BASE = "https://beatsaver.com/api/songs/detail/";
        private const string SONG_BY_HASH_URL_BASE = "https://beatsaver.com/api/songs/search/hash/";

        [JsonIgnore]
        public bool Populated { get; private set; }
        private static readonly Regex _digitRegex = new Regex("^(?:0[xX])?([0-9a-fA-F]+)$", RegexOptions.Compiled);
        private static readonly Regex _oldBeatSaverRegex = new Regex("^[0-9]+-[0-9]+$", RegexOptions.Compiled);

        public object this[string propertyName]
        {
            get
            {
                Type myType = typeof(SongInfo);
                object retVal;
                FieldInfo test = myType.GetField(propertyName);
                if (test != null)
                {
                    retVal = test.GetValue(this);
                }
                else
                {
                    PropertyInfo myPropInfo = myType.GetProperty(propertyName);
                    retVal = myPropInfo.GetValue(this);
                }

                Type whatType = retVal.GetType();
                return retVal;
            }
            set
            {
                Type myType = typeof(SongInfo);
                PropertyInfo myPropInfo = myType.GetProperty(propertyName);
                myPropInfo.SetValue(this, value, null);
            }
        }

        /// <summary>
        /// Downloads the page and returns it as a string.
        /// </summary>
        /// <param name="url"></param>
        /// <exception cref="HttpRequestException"></exception>
        /// <returns></returns>
        public static bool PopulateFields(BeatSaverSong song)
        {
            if (song.Populated)
                return true;
            bool successful = true;
            SongGetMethod searchMethod;
            string url;
            if (!string.IsNullOrEmpty(song.key))
            {
                url = SONG_DETAILS_URL_BASE + song.key;
                searchMethod = SongGetMethod.SongIndex;
            }
            else if (!string.IsNullOrEmpty(song.hash))
            {
                url = SONG_BY_HASH_URL_BASE + song.hash;
                searchMethod = SongGetMethod.Hash;
            }
            else
                return false;
            Task<string> pageReadTask;
            //lock (lockObject)
            pageReadTask = WebUtils.HttpClient.GetStringAsync(url);
            pageReadTask.Wait();
            string pageText = pageReadTask.Result;
            JObject result = new JObject();
            try
            {
                result = JObject.Parse(pageText);
            }
            catch (Exception ex)
            {
                successful = false;
                Logger.Exception("Unable to parse JSON from text", ex);
            }
            if (searchMethod == SongGetMethod.SongIndex)
                JsonConvert.PopulateObject(result["song"].ToString(), song);
            else if (searchMethod == SongGetMethod.Hash)
                JsonConvert.PopulateObject(result["songs"].First.ToString(), song);

            song.Populated = successful;
            return successful;
        }

        public bool PopulateFields()
        {
            return BeatSaverSong.PopulateFields(this);
        }

        enum SongGetMethod
        {
            SongIndex,
            Hash
        }

        /// <summary>
        /// Downloads the page and returns it as a string in an asynchronous operation.
        /// </summary>
        /// <param name="url"></param>
        /// <returns></returns>
        public async Task<bool> PopulateFieldsAsync()
        {
            if (Populated)
                return true;
            SongGetMethod searchMethod;
            string url;
            if (!string.IsNullOrEmpty(key))
            {
                url = SONG_DETAILS_URL_BASE + key;
                searchMethod = SongGetMethod.SongIndex;
            }
            else if (!string.IsNullOrEmpty(hash))
            {
                url = SONG_BY_HASH_URL_BASE + hash;
                searchMethod = SongGetMethod.Hash;
            }
            else
                return false;
            Logger.Debug($"Starting PopulateFieldsAsync for {key}");
            bool successful = true;

            string pageText = "";
            try
            {
                pageText = await WebUtils.HttpClient.GetStringAsync(url).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                Logger.Error($"Timeout occurred while trying to populate fields for {key}");
                return false;
            }
            catch (HttpRequestException)
            {
                Logger.Error($"HttpRequestException while trying to populate fields for {key}");
                return false;
            }
            JObject result = new JObject();
            try
            {
                result = JObject.Parse(pageText);
            }
            catch (JsonReaderException ex)
            {
                Logger.Exception("Unable to parse JSON from text", ex);
            }
            lock (this)
            {
                if (searchMethod == SongGetMethod.SongIndex)
                    JsonConvert.PopulateObject(result["song"].ToString(), this);
                else if (searchMethod == SongGetMethod.Hash)
                    JsonConvert.PopulateObject(result["songs"].First.ToString(), this);
            }
            Logger.Debug($"Finished PopulateFieldsAsync for {key}");
            Populated = successful;
            return successful;
        }

        public BeatSaverSong()
        {
            metadata = new SongMetadata();
            stats = new SongStats();
            uploader = new SongUploader();
        }

        public BeatSaverSong(string songIndex, string songName, string songUrl, string _authorName)
            : this()
        {
            key = songIndex;
            name = songName;
            metadata.levelAuthorName = _authorName;
            downloadURL = songUrl;
        }

        [OnDeserialized]
        protected void OnDeserialized(StreamingContext context)
        {
            Populated = true;
            foreach (var characteristic in metadata.characteristics)
            {
                var toRemove = new List<string>();
                foreach(var diff in characteristic.difficulties)
                {
                    if (diff.Value == null)
                        toRemove.Add(diff.Key);
                }
                toRemove.ForEach(k => characteristic.difficulties.Remove(k));
            }
        }



        public static bool TryParseBeatSaver(JToken token, out BeatSaverSong song)
        {
            string songIndex = token["key"]?.Value<string>();
            if (songIndex == null)
                songIndex = "";
            bool successful = true;
            BeatSaverSong beatSaverSong;
            try
            {
                beatSaverSong = token.ToObject<BeatSaverSong>(new JsonSerializer() {
                    NullValueHandling = NullValueHandling.Ignore,
                    MissingMemberHandling = MissingMemberHandling.Ignore
                });

                song = beatSaverSong;
                //song.EnhancedInfo = enhancedSong;
                //Logger.Debug(song.ToString());
            }
            catch (Exception ex)
            {
                Logger.Exception($"Unable to create a SongInfo from the JSON for {songIndex}\n", ex);
                successful = false;
                song = null;
            }
            return successful;
        }
        /*
        public SongInfo ToSongInfo()
        {
            if (!Populated)
            {
                Logger.Warning("Trying to create SongInfo from an unpopulated SongInfoEnhanced");
                return null;
            }
            if (_songInfo == null)
            {
                if (_beatSaverRegex.IsMatch(key))
                    _songInfo = ScrapedDataProvider.GetSongByKey(key, false);
                if (_songInfo == null)
                    _songInfo = ScrapedDataProvider.GetSongByHash(hashMd5, false);

                if (_songInfo == null)
                {
                    Logger.Info($"Couldn't find song {key} - {name} by {authorName}, generating new song info...");
                    _songInfo = new SongInfo() {
                        key = key,
                        songName = songName,
                        songSubName = songSubName,
                        authorName = authorName,
                        bpm = bpm,
                        playedCount = playedCount,
                        upVotes = upVotes,
                        downVotes = downVotes,
                        hash = hashMd5,
                    };
                    _songInfo.EnhancedInfo = this;
                    ScrapedDataProvider.TryAddToScrapedData(_songInfo);
                    return _songInfo;
                }
                _songInfo.EnhancedInfo = this;
            }
            return _songInfo;
        }
        */
        [Obsolete("Shouldn't use this anymore")]
        public SongInfo GenerateSongInfo()
        {
            var newSong = new SongInfo(hash);
            /*
            var newSong = new SongInfo() {
                key = key,
                songName = metadata.songName,
                songSubName = metadata.songSubName,
                authorName = metadata.levelAuthorName,
                bpm = metadata.bpm,
                playedCount = stats.plays,
                upVotes = stats.upVotes,
                downVotes = stats.downVotes,
                hash = hash,
            };
            */
            //newSong.EnhancedInfo = this;
            return newSong;
        }

        public override string ToString()
        {
            StringBuilder retStr = new StringBuilder();
            retStr.Append("SongInfo:");
            retStr.AppendLine("   Index: " + key);
            retStr.AppendLine("   Name: " + name);
            retStr.AppendLine("   Author: " + metadata.levelAuthorName);
            retStr.AppendLine("   URL: " + downloadURL);
            return retStr.ToString();
        }

        public bool Equals(BeatSaverSong other)
        {
            return hash.ToUpper() == other.hash.ToUpper();
        }

        //[JsonIgnore]
        //private SongInfo _songInfo { get; set; }
        [JsonIgnore]
        public int KeyAsInt
        {
            get
            {
                var match = _digitRegex.Match(key);
                return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber) : 0;
            }
        }
        /*
        [JsonProperty("id")]
        private int _id;
        [JsonIgnore]
        public int id
        {
            get
            {
                if (!(_id > 0))
                    if (key != null && _beatSaverRegex.IsMatch(key))
                        _id = int.Parse(key.Substring(0, key.IndexOf('-')));
                return _id;
            }
            set { _id = value; }
        }
        */
        [JsonProperty("metadata")]
        public SongMetadata metadata { get; }
        [JsonProperty("stats")]
        public SongStats stats { get; }

        [JsonProperty("description")]
        public string description { get; set; }

        [JsonProperty("deletedAt")]
        public DateTime? deletedAt { get; set; }
        [JsonProperty("_id")]
        public string _id { get; set; }
        [JsonIgnore]
        private string _key;
        /// <summary>
        /// Key is always lowercase.
        /// </summary>
        [JsonProperty("key")]
        public string key { get { return _key; } set { _key = value.ToLower(); } }
        [JsonProperty("name")]
        public string name { get; set; }
        [JsonProperty("uploader")]
        public SongUploader uploader { get; }
        [JsonProperty("uploaded")]
        public DateTime uploaded { get; set; }
        [JsonIgnore]
        private string _hash;
        /// <summary>
        /// Hash is always uppercase.
        /// </summary>
        [JsonProperty("hash")]
        public string hash { get { return _hash; } set { _hash = value.ToUpper(); } }

        [JsonProperty("converted")]
        public string converted { get; set; }

        [JsonProperty("downloadURL")]
        public string downloadURL { get; set; }
        [JsonProperty("coverURL")]
        public string coverURL { get; set; }

        [JsonProperty("ScrapedAt")]
        public DateTime ScrapedAt { get; set; }
        /*
        [JsonIgnore]
        private SongMetadata _metadata;
        [JsonIgnore]
        private SongStats _stats;
        [JsonIgnore]
        private SongUploader _uploader;
*/


    }

    public class SongMetadata
    {
        [JsonProperty("difficulties")]
        public Dictionary<string, bool> difficulties;

        [JsonProperty("characteristics")]
        public List<BeatmapCharacteristic> characteristics;

        [JsonProperty("songName")]
        public string songName;

        [JsonProperty("songSubName")]
        public string songSubName;

        [JsonProperty("songAuthorName")]
        public string songAuthorName;

        [JsonProperty("levelAuthorName")]
        public string levelAuthorName;

        [JsonProperty("bpm")]
        public float bpm;
    }

    public class BeatmapCharacteristic
    {
        [JsonProperty("name")]
        public string name { get; set; }
        [JsonProperty("difficulties")]
        public Dictionary<string, DifficultyCharacteristics> difficulties { get; set; }
    }

    [Serializable]
    public class DifficultyCharacteristics
    {
        [JsonProperty("duration")]
        public double duration { get; set; }
        [JsonProperty("length")]
        public int length { get; set; }
        [JsonProperty("bombs")]
        public int bombs { get; set; }
        [JsonProperty("notes")]
        public int notes { get; set; }
        [JsonProperty("obstacles")]
        public int obstacles { get; set; }
        [JsonProperty("njs")]
        public float njs { get; set; }
        [JsonProperty("njsOffset")]
        public float njsOffset { get; set; }
    }

    public class SongStats
    {
        [JsonProperty("downloads")]
        public int downloads;
        [JsonProperty("plays")]
        public int plays;
        [JsonProperty("downVotes")]
        public int downVotes;
        [JsonProperty("upVotes")]
        public int upVotes;
        [JsonProperty("heat")]
        public double heat;
        [JsonProperty("rating")]
        public double rating;
    }

    public class SongUploader
    {
        [JsonProperty("_id")]
        public string id;
        [JsonProperty("username")]
        public string username;
    }


    public static class SongInfoEnhancedExtensions
    {
        public static void PopulateFromBeatSaver(this IEnumerable<BeatSaverSong> songs)
        {
            List<Task> populateTasks = new List<Task>();
            for (int i = 0; i < songs.Count(); i++)
            {
                if (!songs.ElementAt(i).Populated)
                    populateTasks.Add(songs.ElementAt(i).PopulateFieldsAsync());
            }

            Task.WaitAll(populateTasks.ToArray());
        }

        public static async Task PopulateFromBeatSaverAsync(this IEnumerable<BeatSaverSong> songs)
        {
            List<Task> populateTasks = new List<Task>();
            for (int i = 0; i < songs.Count(); i++)
            {
                if (!songs.ElementAt(i).Populated)
                    populateTasks.Add(songs.ElementAt(i).PopulateFieldsAsync());
            }

            await Task.WhenAll(populateTasks).ConfigureAwait(false);
            Logger.Warning("Finished PopulateAsync?");
        }
    }

}

