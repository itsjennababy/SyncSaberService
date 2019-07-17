﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using FeedReader.Logging;
using static FeedReader.WebUtils;

namespace FeedReader
{
    public class BeatSaverReader : IFeedReader
    {
        public static FeedReaderLoggerBase Logger = new FeedReaderLogger(LoggingController.DefaultLogController);
        public static string NameKey => "BeatSaverReader";
        public string Name { get { return NameKey; } }
        public static readonly string SourceKey = "BeatSaver";
        public string Source { get { return SourceKey; } }
        public bool Ready { get; private set; }
        public bool StoreRawData { get; set; }
        #region Constants
        //private static readonly string AUTHORKEY = "{AUTHOR}";
        private static readonly string AUTHORIDKEY = "{AUTHORID}";
        private static readonly string PAGEKEY = "{PAGE}";
        private static readonly string SEARCHTYPEKEY = "{TYPE}";
        private static readonly string SEARCHKEY = "{SEARCH}";
        private const int SONGSPERUSERPAGE = 10;
        private const string INVALIDFEEDSETTINGSMESSAGE = "The IFeedSettings passed is not a BeatSaverFeedSettings.";
        private const string BEATSAVER_DOWNLOAD_URL_BASE = "https://beatsaver.com/api/download/key/";
        private const string BEATSAVER_DETAILS_BASE_URL = "https://beatsaver.com/api/maps/detail/";
        private const string BEATSAVER_GETBYHASH_BASE_URL = "https://beatsaver.com/api/maps/by-hash/";
        private const string BEATSAVER_NIGHTLYDUMP_URL = "https://beatsaver.com/api/download/dumps/maps";
        #endregion

        private static ConcurrentDictionary<string, string> _authors = new ConcurrentDictionary<string, string>();
        // { (BeatSaverFeeds)99, new FeedInfo("search-by-author", "https://beatsaver.com/api/songs/search/user/" + AUTHORKEY) }
        private static Dictionary<BeatSaverFeeds, FeedInfo> _feeds;
        public static Dictionary<BeatSaverFeeds, FeedInfo> Feeds
        {
            get
            {
                if (_feeds == null)
                {
                    _feeds = new Dictionary<BeatSaverFeeds, FeedInfo>()
                    {
                        { (BeatSaverFeeds)0, new FeedInfo("author", "https://beatsaver.com/api/maps/uploader/" +  AUTHORIDKEY + "/" + PAGEKEY)},
                        { (BeatSaverFeeds)1, new FeedInfo("latest", "https://beatsaver.com/api/maps/latest/" + PAGEKEY) },
                        { (BeatSaverFeeds)2, new FeedInfo("hot", "https://beatsaver.com/api/maps/hot/" + PAGEKEY) },
                        { (BeatSaverFeeds)3, new FeedInfo("plays", "https://beatsaver.com/api/maps/plays/" + PAGEKEY) },
                        { (BeatSaverFeeds)4, new FeedInfo("downloads", "https://beatsaver.com/api/maps/downloads/" + PAGEKEY) },
                        { (BeatSaverFeeds)98, new FeedInfo("search", $"https://beatsaver.com/api/search/text/{PAGEKEY}?q={SEARCHKEY}") },
                    };
                }
                return _feeds;
            }
        }

        public void PrepareReader()
        {
            Ready = true;
        }

        public static string GetPageUrl(int feedIndex, int pageIndex = 0, Dictionary<string, string> replacements = null)
        {
            string mapperId = string.Empty;
            StringBuilder url = new StringBuilder(Feeds[(BeatSaverFeeds)feedIndex].BaseUrl);
            //if (!string.IsNullOrEmpty(author) && author.Length > 3)
            //    mapperId = GetAuthorID(author);
            if (replacements != null)
                foreach (var key in replacements.Keys)
                {
                    url.Replace(key, replacements[key]);
                }
            return url.Replace(PAGEKEY, pageIndex.ToString()).ToString();
        }

        /// <summary>
        /// Parses out a List of ScrapedSongs from the given page text. Also works if the page is for a single song.
        /// </summary>
        /// <param name="pageText"></param>
        /// <returns></returns>
        public static List<ScrapedSong> ParseSongsFromPage(string pageText)
        {
            JObject result = new JObject();
            try
            {
                result = JObject.Parse(pageText);

            }
            catch (Exception ex)
            {
                Logger.Exception("Unable to parse JSON from text", ex);
            }
            List<ScrapedSong> songs = new List<ScrapedSong>();
            ScrapedSong newSong;
            int? resultTotal = result["totalDocs"]?.Value<int>();
            if (resultTotal == null) resultTotal = 0;

            // Single song in page text.
            if (resultTotal == 0)
            {
                if (result["key"] != null)
                {
                    newSong = ParseSongFromJson(result);
                    if (newSong != null)
                    {
                        songs.Add(newSong);
                        return songs;
                    }
                }
                return songs;
            }

            // Array of songs in page text.
            var songJSONAry = result["docs"]?.ToArray();

            if (songJSONAry == null)
            {
                Logger.Error("Invalid page text: 'songs' field not found.");
                return songs;
            }

            foreach (JObject song in songJSONAry)
            {
                newSong = ParseSongFromJson(song);
                if (newSong != null)
                    songs.Add(newSong);
            }
            return songs;
        }

        /// <summary>
        /// Creates a SongInfo from a JObject.
        /// </summary>
        /// <param name="song"></param>
        /// <exception cref="ArgumentException">Thrown when a hash can't be found for the given song JObject.</exception>
        /// <returns></returns>
        public static ScrapedSong ParseSongFromJson(JObject song)
        {
            //JSONObject song = (JSONObject) aKeyValue;
            string songKey = song["key"]?.Value<string>();
            string songHash = song["hash"]?.Value<string>().ToUpper();
            var songName = song["name"]?.Value<string>();
            var mapperName = song["uploader"]?["username"]?.Value<string>();
            if (string.IsNullOrEmpty(songHash))
                throw new ArgumentException("Unable to find hash for the provided song, is this a valid song JObject?");
            string songUrl = !string.IsNullOrEmpty(songKey) ? BEATSAVER_DOWNLOAD_URL_BASE + songKey : string.Empty;
            var newSong = new ScrapedSong(songHash)
            {
                DownloadUrl = songUrl,
                SongName = songName,
                MapperName = mapperName,
                RawData = song.ToString()
            };
            return newSong;
        }

        #region Web Requests

        #region Async
        public async Task<Dictionary<string, ScrapedSong>> GetSongsFromFeedAsync(IFeedSettings _settings, CancellationToken cancellationToken)
        {
            PrepareReader();
            if (!(_settings is BeatSaverFeedSettings settings))
                throw new InvalidCastException(INVALIDFEEDSETTINGSMESSAGE);
            List<ScrapedSong> songs = new List<ScrapedSong>();

            switch ((BeatSaverFeeds)settings.FeedIndex)
            {
                // Author
                case BeatSaverFeeds.AUTHOR:
                    List<ScrapedSong> newSongs = null;
                    string songSource = string.Empty;
                    foreach (var author in settings.Authors)
                    {
                        newSongs = await GetSongsByAuthorAsync(author).ConfigureAwait(false);
                        songSource = "Beat Saver";
                        songs.AddRange(newSongs);

                        Logger.Info($"Found {newSongs.Count} songs uploaded by {author} from {songSource}");
                    }
                    break;
                // Newest
                case BeatSaverFeeds.LATEST:
                    songs.AddRange(await GetNewestSongsAsync(settings).ConfigureAwait(false));
                    break;
                // Top
                case BeatSaverFeeds.HOT:
                    break;
                default:
                    break;
            }

            Dictionary<string, ScrapedSong> retDict = new Dictionary<string, ScrapedSong>();
            foreach (var song in songs)
            {
                if (!retDict.ContainsKey(song.Hash))
                {
                    retDict.Add(song.Hash, song);
                }
            }
            return retDict;
        }
        public async Task<Dictionary<string, ScrapedSong>> GetSongsFromFeedAsync(IFeedSettings settings)
        {
            return await GetSongsFromFeedAsync(settings, CancellationToken.None).ConfigureAwait(false);
        }
        public async Task<List<ScrapedSong>> GetNewestSongsAsync(BeatSaverFeedSettings settings)
        {
            int feedIndex = 1;
            bool useMaxPages = settings.MaxPages != 0;
            List<ScrapedSong> songs = new List<ScrapedSong>();
            string pageText = string.Empty;
            using (var response = await GetPageAsync(GetPageUrl(feedIndex)).ConfigureAwait(false))
            {
                if (response.IsSuccessStatusCode)
                    pageText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                else
                    return songs;
            }

            JObject result = new JObject();
            try
            {
                result = JObject.Parse(pageText);
            }
            catch (Exception ex)
            {
                Logger.Exception("Unable to parse JSON from text", ex);
            }
            string mapperId = string.Empty;
            int? numSongs = result["totalDocs"]?.Value<int>();
            int? lastPage = result["lastPage"]?.Value<int>();
            if (numSongs == null || lastPage == null || numSongs == 0)
            {
                Logger.Warning($"Error checking Beat Saver's Latest feed.");
                return songs;
            }
            Logger.Info($"Checking Beat Saver's Latest feed, {numSongs} songs available");
            int songCount = 0;
            int pageNum = 0;
            List<Task<List<ScrapedSong>>> pageReadTasks = new List<Task<List<ScrapedSong>>>();
            string url = "";
            bool continueLooping = true;
            do
            {
                songCount = songs.Count;
                url = GetPageUrl(feedIndex, pageNum);
                Logger.Debug($"Creating task for {url}");
                pageReadTasks.Add(GetSongsFromPageAsync(url));
                pageNum++;
                if ((pageNum > lastPage))
                    continueLooping = false;
                if (useMaxPages && (pageNum >= settings.MaxPages))
                    continueLooping = false;
            } while (continueLooping);
            try
            {
                await Task.WhenAll(pageReadTasks.ToArray()).ConfigureAwait(false);
            }
            catch (Exception)
            {
                Logger.Error($"Error waiting for pageReadTasks");
            }
            foreach (var job in pageReadTasks)
            {
                songs.AddRange(await job);
            }
            return songs;
        }

        public static async Task<List<string>> GetAuthorNamesByIDAsync(string mapperId)
        {
            List<string> authorNames = new List<string>();
            List<ScrapedSong> songs = await GetSongsByUploaderIdAsync(mapperId).ConfigureAwait(false);
            authorNames = songs.Select(s => s.MapperName).Distinct().ToList();
            //authorNames.ForEach(n => Logger.Warning($"Found authorName: {n}"));
            return authorNames;
        }

        public static async Task<string> GetAuthorIDAsync(string authorName)
        {
            if (_authors.ContainsKey(authorName))
                return _authors[authorName];
            string mapperId = string.Empty;

            int page = 0;
            int? totalResults;
            string searchURL, pageText;
            JObject result;
            JToken matchingSong;
            JToken[] songJSONAry;
            do
            {
                Logger.Debug($"Checking page {page + 1} for the author ID.");
                searchURL = Feeds[BeatSaverFeeds.SEARCH].BaseUrl.Replace(SEARCHKEY, authorName).Replace(PAGEKEY, (page * SONGSPERUSERPAGE).ToString());

                using (var response = await WebUtils.GetPageAsync(searchURL).ConfigureAwait(false))
                {
                    if (response.IsSuccessStatusCode)
                        pageText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    else
                    {
                        Logger.Error($"Error getting UploaderID from author name, {searchURL} responded with {response.StatusCode}:{response.ReasonPhrase}");
                        return string.Empty;
                    }
                }

                result = new JObject();
                try { result = JObject.Parse(pageText); }
                catch (Exception ex)
                {
                    Logger.Exception("Unable to parse JSON from text", ex);
                }
                totalResults = result["totalDocs"]?.Value<int>(); // TODO: Check this
                if (totalResults == null || totalResults == 0)
                {
                    Logger.Warning($"No songs by {authorName} found, is the name spelled correctly?");
                    return string.Empty;
                }
                songJSONAry = result["docs"].ToArray();
                matchingSong = (JObject)songJSONAry.FirstOrDefault(c => c["uploader"]?["username"]?.Value<string>()?.ToLower() == authorName.ToLower());

                page++;
                searchURL = Feeds[BeatSaverFeeds.SEARCH].BaseUrl.Replace(SEARCHKEY, authorName).Replace(PAGEKEY, (page * SONGSPERUSERPAGE).ToString());
            } while ((matchingSong == null) && page * SONGSPERUSERPAGE < totalResults);


            if (matchingSong == null)
            {
                Logger.Warning($"No songs by {authorName} found, is the name spelled correctly?");
                return string.Empty;
            }
            mapperId = matchingSong["uploader"]["_id"].Value<string>();
            _authors.TryAdd(authorName, mapperId);

            return mapperId;
        }
        public static async Task<List<ScrapedSong>> GetSongsFromPageAsync(string url)
        {
            string pageText = string.Empty;
            var songs = new List<ScrapedSong>();
            using (var response = await WebUtils.GetPageAsync(url).ConfigureAwait(false))
            {
                if (response.IsSuccessStatusCode)
                    pageText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                else
                {
                    Logger.Error($"Error getting songs from page, {url} responded with {response.StatusCode}:{response.ReasonPhrase}");
                    return songs;
                }
            }

            foreach (var song in ParseSongsFromPage(pageText))
            {
                songs.Add(song);
            }
            return songs;
        }
        /// <summary>
        /// Gets a list of songs by an author with the provided ID (NOT the author's username).
        /// </summary>
        /// <param name="authorId"></param>
        /// <returns></returns>
        public static async Task<List<ScrapedSong>> GetSongsByUploaderIdAsync(string authorId)
        {
            int feedIndex = 0;
            List<ScrapedSong> songs = new List<ScrapedSong>();
            string pageText = string.Empty;
            string url = GetPageUrl(feedIndex, 0, new Dictionary<string, string>() { { AUTHORIDKEY, authorId } });
            try
            {
                using (var response = await GetPageAsync(url).ConfigureAwait(false))
                {
                    if (response.IsSuccessStatusCode)
                        pageText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    else
                    {
                        Logger.Error($"Error getting songs by UploaderId, {url} responded with {response.StatusCode}:{response.ReasonPhrase}");
                        return songs;
                    }
                }
            }
            catch (HttpGetException ex)
            {
                Logger.Error($"Error getting songs by UploaderId, {ex.Url} responded with {ex.HttpStatusCode.ToString()}");
                return songs;
            }
            catch (Exception ex)
            {
                Logger.Exception($"Error getting songs by UploaderId, {authorId}, from {url}", ex);
                return songs;
            }

            JObject result = new JObject();
            try
            {
                result = JObject.Parse(pageText);
            }
            catch (Exception ex)
            {
                Logger.Exception("Unable to parse JSON from text", ex);
                return songs;
            }

            int numSongs = result["totalDocs"]?.Value<int>() ?? 0; // Check this
            int lastPage = result["lastPage"]?.Value<int>() ?? 0;
            Logger.Info($"Found {numSongs} songs by {authorId} on Beat Saver");
            int pageNum = 0;
            List<Task<List<ScrapedSong>>> pageReadTasks = new List<Task<List<ScrapedSong>>>();
            do
            {
                url = GetPageUrl(feedIndex, pageNum, new Dictionary<string, string>() { { AUTHORIDKEY, authorId } });
                Logger.Debug($"Creating task for {url}");
                pageReadTasks.Add(GetSongsFromPageAsync(url));
                pageNum++;
            } while (pageNum <= lastPage);

            await Task.WhenAll(pageReadTasks.ToArray()).ConfigureAwait(false);
            foreach (var job in pageReadTasks)
            {
                songs.AddRange(await job);
            }
            return songs;
        }
        public static async Task<List<ScrapedSong>> GetSongsByAuthorAsync(string uploader)
        {
            string mapperId = await GetAuthorIDAsync(uploader).ConfigureAwait(false);
            if (string.IsNullOrEmpty(mapperId))
                return new List<ScrapedSong>();
            return await GetSongsByUploaderIdAsync(mapperId).ConfigureAwait(false);
        }
        public static async Task<ScrapedSong> GetSongByHashAsync(string hash)
        {
            string url = BEATSAVER_GETBYHASH_BASE_URL + hash;
            string pageText = "";
            ScrapedSong song = null;
            try
            {
                using (var response = await WebUtils.GetPageAsync(url).ConfigureAwait(false))
                {
                    if (response.IsSuccessStatusCode)
                        pageText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    else
                    {
                        Logger.Error($"Error getting song by hash, {url} responded with {response.StatusCode}:{response.ReasonPhrase}");
                        return song;
                    }
                }
                if (string.IsNullOrEmpty(pageText))
                {
                    Logger.Warning($"Unable to get web page at {url}");
                    return null;
                }
            }
            catch (HttpRequestException)
            {
                Logger.Error($"HttpRequestException while trying to populate fields for {hash}");
                return null;
            }
            catch (AggregateException ae)
            {
                ae.WriteExceptions($"Exception while trying to get details for {hash}");
            }
            catch (Exception ex)
            {
                Logger.Exception("Exception getting page", ex);
            }
            song = ParseSongsFromPage(pageText).FirstOrDefault();
            return song;
        }
        public static async Task<ScrapedSong> GetSongByKeyAsync(string key)
        {
            string url = BEATSAVER_DETAILS_BASE_URL + key;
            string pageText = "";
            ScrapedSong song = null;
            try
            {
                using (var response = await WebUtils.GetPageAsync(url).ConfigureAwait(false))
                {
                    if (response.IsSuccessStatusCode)
                        pageText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    else
                    {
                        Logger.Error($"Error getting song by key, {url} responded with {response.StatusCode}:{response.ReasonPhrase}");
                        return song;
                    }
                }
                if (string.IsNullOrEmpty(pageText))
                {
                    Logger.Warning($"Unable to get web page at {url}");
                    return null;
                }
            }
            catch (HttpRequestException)
            {
                Logger.Error($"HttpRequestException while trying to populate fields for {key}");
                return null;
            }
            catch (AggregateException ae)
            {
                ae.WriteExceptions($"Exception while trying to get details for {key}");
            }
            catch (Exception ex)
            {
                Logger.Exception("Exception getting page", ex);
            }
            song = ParseSongsFromPage(pageText).FirstOrDefault();
            return song;
        }
        public static async Task<List<ScrapedSong>> SearchAsync(string criteria, SearchType type)
        {

            if (type == SearchType.key)
            {
                return new List<ScrapedSong>() { await GetSongByKeyAsync(criteria).ConfigureAwait(false) };
            }

            if (type == SearchType.user)
            {
                return await GetSongsByUploaderIdAsync((await GetAuthorNamesByIDAsync(criteria).ConfigureAwait(false)).FirstOrDefault()).ConfigureAwait(false);
            }

            if (type == SearchType.hash)
            {
                return new List<ScrapedSong>() { await GetSongByHashAsync(criteria).ConfigureAwait(false) };
            }
            StringBuilder url;
            url = new StringBuilder(Feeds[BeatSaverFeeds.SEARCH].BaseUrl);
            url.Replace(SEARCHTYPEKEY, type.ToString());
            url.Replace(SEARCHKEY, criteria);
            string pageText = string.Empty;
            var songs = new List<ScrapedSong>();
            using (var response = await WebUtils.GetPageAsync(url.ToString()).ConfigureAwait(false))
            {
                if (response.IsSuccessStatusCode)
                    pageText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                else
                {
                    Logger.Error($"Error getting song by key, {url.ToString()} responded with {response.StatusCode}:{response.ReasonPhrase}");
                    return songs;
                }
            }
            songs = ParseSongsFromPage(pageText);

            return songs;
        }
        #endregion

        #region Sync
        /// <summary>
        /// Retrieves the songs from a feed with the given settings in the form of a Dictionary, with the key being the song's hash and a ScrapedSong as the value.
        /// </summary>
        /// <param name="_settings"></param>
        /// <exception cref="InvalidCastException">Thrown when the passed IFeedSettings isn't a BeatSaverFeedSettings</exception>
        /// <returns></returns>
        public Dictionary<string, ScrapedSong> GetSongsFromFeed(IFeedSettings _settings)
        {
            return GetSongsFromFeedAsync(_settings).Result;
        }
        public List<ScrapedSong> GetNewestSongs(BeatSaverFeedSettings settings)
        {
            return GetNewestSongsAsync(settings).Result;
        }

        public static List<string> GetAuthorNamesByID(string mapperId)
        {
            return GetAuthorNamesByIDAsync(mapperId).Result;
        }
        public static string GetAuthorID(string authorName)
        {
            return GetAuthorIDAsync(authorName).Result;
        }
        public static List<ScrapedSong> GetSongsFromPage(string url)
        {
            return GetSongsFromPageAsync(url).Result;
        }
        [Obsolete("Check this")]
        public static List<ScrapedSong> GetSongsByUploaderId(string authorId)
        {
            return GetSongsByUploaderIdAsync(authorId).Result;
        }
        /// <summary>
        /// Searches Beat Saver and retrieves all songs by the provided uploader name.
        /// </summary>
        /// <param name="uploader"></param>
        /// <returns></returns>
        public static List<ScrapedSong> GetSongsByAuthor(string uploader)
        {
            return GetSongsByAuthorAsync(uploader).Result;
        }
        public static ScrapedSong GetSongByHash(string hash)
        {
            return GetSongByHashAsync(hash).Result;
        }
        public static ScrapedSong GetSongByKey(string key)
        {
            return GetSongByKeyAsync(key).Result;
        }
        public static List<ScrapedSong> Search(string criteria, SearchType type)
        {
            return SearchAsync(criteria, type).Result;
        }


        #endregion

        #endregion

        public enum SearchType
        {
            author, // author name (not necessarily uploader)
            name, // song name only
            user, // user (uploader) name
            hash, // MD5 Hash
            song, // song name, song subname, author 
            key,
            all // name, user, song
        }

    }

    public class BeatSaverFeedSettings : IFeedSettings
    {
        public int _feedIndex;
        public int MaxPages = 0;
        public string[] Authors;
        public string FeedName { get { return BeatSaverReader.Feeds[Feed].Name; } }
        public BeatSaverFeeds Feed { get { return (BeatSaverFeeds)FeedIndex; } set { _feedIndex = (int)value; } }
        public int FeedIndex { get { return _feedIndex; } }
        public bool UseSongKeyAsOutputFolder { get; set; }
        public bool searchOnline { get; set; }
        public int MaxSongs { get; set; }

        public BeatSaverFeedSettings(int feedIndex)
        {
            searchOnline = false;
            _feedIndex = feedIndex;
            UseSongKeyAsOutputFolder = true;
        }
    }

    public enum BeatSaverFeeds
    {
        AUTHOR = 0,
        LATEST = 1,
        HOT = 2,
        PLAYS = 3,
        DOWNLOADS = 4,
        SEARCH = 98,
    }
}