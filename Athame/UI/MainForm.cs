﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Athame.DownloadAndTag;
using Athame.Logging;
using Athame.Plugin;
using Athame.PluginAPI.Downloader;
using Athame.PluginAPI.Service;
using Athame.Properties;
using Athame.Settings;
using Athame.UI.Win32;
using Athame.Utils;
using Microsoft.WindowsAPICodePack.Dialogs;
using Microsoft.WindowsAPICodePack.Taskbar;

namespace Athame.UI
{
    public partial class MainForm : Form
    {
        /// <summary>
        /// Internal class for keeping track of an individual listitem's relation
        /// </summary>
        private class MediaItemTag
        {
            public EnqueuedCollection Collection { get; set; }
            public Track Track { get; set; }
            public int IndexInCollection { get; set; }
            public int GroupIndex { get; set; }
            public int GlobalItemIndex { get; set; }
            public Exception Exception { get; set; }
        }

        // Constants
        private new const string Tag = nameof(MainForm);
        private const string GroupHeaderFormat = "{0}: {1} ({2})";
        private const int RetryCount = 3;

        // Read-only instance vars
        private readonly TaskbarManager mTaskbarManager = TaskbarManager.Instance;
        private readonly MediaDownloadQueue mediaDownloadQueue = new MediaDownloadQueue(true);

        // Instance vars
        private UrlParseResult mResult;
        private MusicService mService;
        private ListViewItem mCurrentlySelectedQueueItem;
        private ListViewItem currentlyDownloadingItem;
        private CollectionDownloadEventArgs currentCollection;
        private bool isListViewDirty = false;
        private int numberOfRetries = 0;
        

        public MainForm()
        {
            InitializeComponent();
            UnlockUi();
            // The formula (1 / x) * 1000 where x = FPS will give us our timer interval in regards
            // to how fast we want the animation to show in FPS
            queueImageAnimationTimer.Interval = (int)(((double)1 / 12) * 1000);

            // Add event handlers for MDQ
            mediaDownloadQueue.Exception += MediaDownloadQueue_Exception;
            mediaDownloadQueue.CollectionDequeued += MediaDownloadQueue_CollectionDequeued;
            mediaDownloadQueue.TrackDequeued += MediaDownloadQueue_TrackDequeued;
            mediaDownloadQueue.TrackDownloadCompleted += MediaDownloadQueue_TrackDownloadCompleted;
            mediaDownloadQueue.TrackDownloadProgress += MediaDownloadQueue_TrackDownloadProgress;

            // Error handler for plugin loader
            Program.DefaultPluginManager.LoadException += DefaultPluginManagerOnLoadException;
        }

        private List<Exception> pluginLoadExceptions = new List<Exception>();

        private void DefaultPluginManagerOnLoadException(object sender, PluginLoadExceptionEventArgs pluginLoadExceptionEventArgs)
        {
            if (pluginLoadExceptionEventArgs.Exception.GetType() == typeof(PluginIncompatibleException))
            {
                CommonTaskDialogs.Message("Incompatible plugin",
                    $"The plugin \"{pluginLoadExceptionEventArgs.PluginName}\" is incompatible with this version of Athame.",
                    icon: TaskDialogStandardIcon.Error, owner: this).Show();
            }
            else
            {
                pluginLoadExceptions.Add(pluginLoadExceptionEventArgs.Exception);
            }
            pluginLoadExceptionEventArgs.Continue = true;
        }

        /// <summary>
        /// Rounds a decimal less than 1 up then multiplies it by 100. If the result is greater than 100, returns 100, otherwise returns the result.
        /// </summary>
        /// <param name="percent">The percent value to convert.</param>
        /// <returns>An integer that is not greater than 100.</returns>
        private int PercentToInt(decimal percent)
        {
            var rounded = (int) (Decimal.Round(percent, 2, MidpointRounding.ToEven) * (decimal) 100);
            return rounded > 100 ? 100 : rounded;
        }

        private void MediaDownloadQueue_TrackDownloadProgress(object sender, TrackDownloadEventArgs e)
        {
            collectionProgressBar.Value = PercentToInt(e.TotalProgress);
            totalProgressBar.Value +=
                PercentToInt(((decimal) (e.TotalProgress + currentCollection.CurrentCollectionIndex) /
                              currentCollection.TotalNumberOfCollections)) - totalProgressBar.Value;
            SetGlobalProgress(totalProgressBar.Value);
            switch (e.State)
            {
                case DownloadState.PreProcess:
                    StartAnimation(currentlyDownloadingItem);
                    currentlyDownloadingItem.Text = "Downloading collection...";
                    collectionStatusLabel.Text = "Pre-processing...";
                    break;
                case DownloadState.DownloadingAlbumArtwork:
                    collectionStatusLabel.Text = "Downloading album artwork...";
                    break;
                case DownloadState.Downloading:
                    collectionStatusLabel.Text = $"[{totalProgressBar.Value}%] Downloading track...";
                    break;
                case DownloadState.PostProcess:
                    collectionStatusLabel.Text = "Post-processing...";
                    break;
                case DownloadState.WritingTags:
                    collectionStatusLabel.Text = "Writing tags...";
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void MediaDownloadQueue_TrackDownloadCompleted(object sender, TrackDownloadEventArgs e)
        {
            collectionStatusLabel.Text = "Completed";
            StopAnimation();
            currentlyDownloadingItem.ImageKey = "done";
            currentlyDownloadingItem.Text = "Completed";
        }

        private void MediaDownloadQueue_TrackDequeued(object sender, TrackDownloadEventArgs e)
        {
            numberOfRetries = 0;
            // this'll bite me in the ass someday
            currentlyDownloadingItem = queueListView.Groups[currentCollection.CurrentCollectionIndex].Items[e.CurrentItemIndex * 2];
            queueListView.EnsureVisible(currentlyDownloadingItem.Index);
        }

        private void MediaDownloadQueue_CollectionDequeued(object sender, CollectionDownloadEventArgs e)
        {
            currentCollection = e;
            totalStatusLabel.Text = $"{e.CurrentCollectionIndex + 1}/{e.TotalNumberOfCollections}: {MediaCollectionAsType(e.Collection.MediaCollection)} \"{e.Collection.MediaCollection.Title}\"";
        }

        private void MediaDownloadQueue_Exception(object sender, ExceptionEventArgs e)
        {
            Log.WriteException(Level.Error, Tag, e.Exception, "MDQ exception handler");
            var tag = (MediaItemTag)currentlyDownloadingItem?.Tag;
            if (tag == null)
            {
                Log.Error(Tag, "MDQ exception handler: currently downloading LV item tag is null!");
                throw e.Exception;
            }
            StopAnimation();
            currentlyDownloadingItem.ImageKey = "error";
            currentlyDownloadingItem.Text = "Error occurred while downloading";
            tag.Exception = e.Exception;
            e.SkipTo = ExceptionSkip.Item;
            
        }

        private string BuildFlags(IEnumerable<Metadata> metadata)
        {
            if (metadata == null) return "";
            var ret = new List<string>();
            foreach (var data in metadata)
            {
                if (!data.CanDisplay) continue;
                if (data.IsFlag)
                {
                    // If it's a flag, only display it if the value is "True" (i.e. Boolean.ToString())
                    if (data.Value == Boolean.TrueString)
                    {
                        ret.Add(data.Name);
                    }
                }
                else
                {
                    ret.Add($"{data.Name}={data.Value}");
                }
            }
            return String.Join(", ", ret);
        }

        #region Download queue manipulation
        private void AddToQueue(MusicService service, IMediaCollection item, string pathFormat)
        {
            var enqueuedItem = mediaDownloadQueue.Enqueue(service, item, pathFormat);
            var mediaType = MediaCollectionAsType(item);
            var header = String.Format(GroupHeaderFormat, mediaType, item.Title, service.Info.Name);
            var group = new ListViewGroup(header);
            var groupIndex = queueListView.Groups.Add(group);
            for (var i = 0; i < item.Tracks.Count; i++)
            {
                var t = item.Tracks[i];
                var lvItem = new ListViewItem
                {
                    Group = group,
                    Tag = new MediaItemTag
                    {
                        Track = t,
                        Collection = enqueuedItem,
                        GroupIndex = groupIndex,
                        IndexInCollection = i
                    }
                };
                if (!t.IsDownloadable)
                {
                    Log.Warning(Tag, $"Adding non-downloadable track {service.Info.Name}/{t.Id}");
                    lvItem.BackColor = SystemColors.Control;
                    lvItem.ForeColor = SystemColors.GrayText;
                    lvItem.ImageKey = "not_downloadable";
                    lvItem.Text = "Unavailable";
                }
                else
                {
                    lvItem.ImageKey = "ready";
                    lvItem.Text = "Ready to download";
                }
                lvItem.SubItems.Add(t.DiscNumber + " / " + t.TrackNumber);
                lvItem.SubItems.Add(t.Title);
                lvItem.SubItems.Add(t.Artist.Name);
                lvItem.SubItems.Add(t.Album.Title);
                lvItem.SubItems.Add(BuildFlags(t.CustomMetadata));
                lvItem.SubItems.Add(t.GetBasicPath(enqueuedItem.PathFormat));
                group.Items.Add(lvItem);
                queueListView.Items.Add(lvItem);
            }
        }

        private void RemoveCurrentlySelectedTracks()
        {
            if (mCurrentlySelectedQueueItem == null) return;
            var selectedItemsList = queueListView.SelectedItems.Cast<ListViewItem>().ToList();
            foreach (var listViewItem in selectedItemsList)
            {
                var item = (MediaItemTag)listViewItem.Tag;
                item.Collection.MediaCollection.Tracks.Remove(item.Track);
                queueListView.Items.Remove(listViewItem);
                if (item.Collection.MediaCollection.Tracks.Count == 0)
                {
                    mediaDownloadQueue.Remove(item.Collection);
                }
            }
            mCurrentlySelectedQueueItem = null;
        }

        private void RemoveCurrentlySelectedGroup()
        {
            if (mCurrentlySelectedQueueItem == null) return;
            // Remove internal queue item
            var item = (MediaItemTag)mCurrentlySelectedQueueItem.Tag;
            mediaDownloadQueue.Remove(item.Collection);
            // We have to remove the group from the ListView first...
            var group = mCurrentlySelectedQueueItem.Group;
            queueListView.Groups.Remove(group);
            // ...then remove all the items
            foreach (var groupItem in group.Items)
            {
                queueListView.Items.Remove((ListViewItem)groupItem);
            }
            mCurrentlySelectedQueueItem = null;
        }
        #endregion

        private MediaType MediaCollectionAsType(IMediaCollection collection)
        {
            if (collection is Album)
            {
                return MediaType.Album;
            }
            if (collection is Playlist)
            {
                return MediaType.Playlist;
            }
            if (collection is SingleTrackCollection)
            {
                return MediaType.Track;
            }
            return MediaType.Unknown;
        }

        private void LockUi()
        {
            idTextBox.Enabled = false;
            dlButton.Enabled = false;
            settingsButton.Enabled = false;
            pasteButton.Enabled = false;
            clearButton.Enabled = false;
            startDownloadButton.Enabled = false;
            queueListView.Enabled = false;
        }

        private void UnlockUi()
        {
            idTextBox.Enabled = true;
            dlButton.Enabled = !String.IsNullOrWhiteSpace(idTextBox.Text);
            settingsButton.Enabled = true;
            pasteButton.Enabled = true;
            clearButton.Enabled = true;
            startDownloadButton.Enabled = mediaDownloadQueue.Count > 0;
            queueListView.Enabled = true;
        }

        private void SetGlobalProgress(int value)
        {
            if (value == 0)
            {
                mTaskbarManager.SetProgressState(TaskbarProgressBarState.NoProgress);
            }
            mTaskbarManager?.SetProgressValue(value, totalProgressBar.Maximum);
        }

        private void SetGlobalProgressState(ProgressBarState state)
        {
            switch (state)
            {
                case ProgressBarState.Normal:
                    mTaskbarManager?.SetProgressState(TaskbarProgressBarState.Normal);
                    break;
                case ProgressBarState.Error:
                    mTaskbarManager?.SetProgressState(TaskbarProgressBarState.Error);
                    break;
                case ProgressBarState.Warning:
                    mTaskbarManager?.SetProgressState(TaskbarProgressBarState.Paused);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(state), state, null);
            }
        }

        private void PresentException(Exception ex)
        {

            Log.WriteException(Level.Error, Tag, ex, "PresentException");
            SetGlobalProgressState(ProgressBarState.Error);
            var th = "An unknown error occurred";
            if (ex is ResourceNotFoundException)
            {
                th = "Resource not found";
            }
            else if (ex is InvalidSessionException)
            {
                th = "Invalid session/subscription expired";
            }
            CommonTaskDialogs.Exception(ex, th, "You may need to sign into this service again.", this).Show();
        }

        private MediaTypeSavePreference PreferenceForType(MediaType type)
        {
            if (type == MediaType.Playlist && !Program.DefaultSettings.Settings.PlaylistSavePreferenceUsesGeneral)
            {
                return Program.DefaultSettings.Settings.PlaylistSavePreference;
            }
            return Program.DefaultSettings.Settings.GeneralSavePreference.Clone();
        }
        private static bool IsWithinVisibleBounds(Point topLeft)
        {
            var screens = Screen.AllScreens;
            return (from screen in screens
                    where screen.WorkingArea.Contains(topLeft)
                    select screen).Any();
        }

        private void RestoreServices()
        {
            var td = CommonTaskDialogs.Wait(owner: this);
            var openCt = new CancellationTokenSource();
            td.Opened += async (o, args) =>
            {
                await Task.Factory.StartNew(async () =>
                {
                    foreach (var service in Program.DefaultPluginManager.ServicesEnumerable())
                    {
                        var restorable = service.AsAuthenticatable();
                        if (restorable == null || !restorable.HasSavedSession) continue;

                        var result = false;
                        td.InstructionText = $"Signing into {service.Info.Name}...";
                        td.Text = $"Signing in as {LocalisableAccountNameFormat.GetFormattedName(restorable.Account)}";
                        openCt.Token.ThrowIfCancellationRequested();
                        result = await restorable.RestoreAsync();
                        if (!result)
                        {
                            Log.Error(Tag, $"Failed to sign into {service.Info.Name}");
                            CommonTaskDialogs.Message(owner: this, caption: $"Failed to sign in to {service.Info.Name}",
                                message: null, icon: TaskDialogStandardIcon.Error).Show();
                        }
                    }
                    td.Close();
                }, openCt.Token);

            };
            if (td.Show() == TaskDialogResult.Cancel)
            {
                openCt.Cancel(true);
            }
        }

#region Validation for URL
        private const string UrlInvalid = "Invalid URL. Check that the URL begins with \"http://\" or \"https://\".";
        private const string UrlNoService = "Can't download this URL.";
        private const string UrlNeedsAuthentication = "You need to sign in to {0} first. " + UrlNeedsAuthenticationLink1;
        private const string UrlNeedsAuthenticationLink1 = "Click here to sign in.";
        private const string UrlNotParseable = "The URL does not point to a valid track, album, artist or playlist.";
        private const string UrlValidParseResult = "{0} from {1}";

        private bool ValidateEnteredUrl()
        {
            urlValidStateLabel.ResetText();
            urlValidStateLabel.Links.Clear();
            urlValidStateLabel.Image = Resources.error;
            dlButton.Enabled = false;

            // Hide on empty
            if (String.IsNullOrWhiteSpace(idTextBox.Text))
            {
                urlValidStateLabel.Visible = false;
                return false;
            }
            urlValidStateLabel.Visible = true;

            Uri url;
            // Invalid URL
            if (!Uri.TryCreate(idTextBox.Text, UriKind.Absolute, out url))
            {
                urlValidStateLabel.Text = UrlInvalid;
                return false;
            }
            var service = Program.DefaultPluginManager.GetServiceByBaseUri(url);
            // No service associated with host
            if (service == null)
            {
                urlValidStateLabel.Text = UrlNoService;
                return false;
            }
            // Not authenticated
            var authenticatable = service.AsAuthenticatable();
            if (!authenticatable.IsAuthenticated)
            {
                urlValidStateLabel.Text = String.Format(UrlNeedsAuthentication, service.Info.Name);
                var linkIndex = urlValidStateLabel.Text.LastIndexOf(UrlNeedsAuthenticationLink1, StringComparison.Ordinal);
                urlValidStateLabel.Links.Add(linkIndex, urlValidStateLabel.Text.Length, service);
                return false;
            }
            // URL doesn't point to media
            var result = service.ParseUrl(url);
            if (result == null)
            {
                urlValidStateLabel.Text = UrlNotParseable;
                return false;
            }
            // Success
            urlValidStateLabel.Image = Resources.done;
            urlValidStateLabel.Text = String.Format(UrlValidParseResult, result.Type, service.Info.Name);
            dlButton.Enabled = true;
            mResult = result;
            mService = service;
            return true;
        }
#endregion

#region Easter egg

        private readonly string[] messages = { "Woo-hoo!", "We did it!", "Yusssss", "Alright!", "Sweet!", "Nice...." };
        private readonly Random random = new Random();

        private string GetCompletionMessage()
        {
            var messagesList = messages.ToList();
            if (DateTime.Now.DayOfWeek == DayOfWeek.Friday)
            {
                messagesList.Add("It's Friday, baby!");
            }
            return messagesList[random.Next(messagesList.Count)];
        }

#endregion

        private void CleanQueueListView()
        {
            collectionStatusLabel.Text = "Ready to begin.";
            collectionProgressBar.Value = 0;
            SetGlobalProgress(0);
            SetGlobalProgressState(ProgressBarState.Normal);
            totalProgressBar.Value = 0;
            totalStatusLabel.Text = "Ready";
            queueListView.Groups.Clear();
            queueListView.Items.Clear();
            isListViewDirty = false;
        }

#region MainForm event handlers and control event handlers
        
        private void button1_Click(object sender, EventArgs e)
        {
            if (isListViewDirty)
            {
                CleanQueueListView();
            }
#if !DEBUG
            try
            {
#endif
                // Don't add if the item is already enqueued.
                var isAlreadyInQueue = mediaDownloadQueue.ItemById(mResult.Id) != null;
                if (isAlreadyInQueue)
                {
                    using (
                        var td = CommonTaskDialogs.Message(owner: this, icon: TaskDialogStandardIcon.Error, 
                        caption: "Cannot add to download queue", message: "This item already exists in the download queue."))
                    {
                        td.Show();
                        return;
                    }
                }

                // Ask for the location if required before we begin retrieval
                var prefType = PreferenceForType(mResult.Type);
                var saveDir = prefType.SaveDirectory;
                if (prefType.AskForLocation)
                {
                    using (var folderSelectionDialog = new FolderBrowserDialog { Description = "Select a destionation for this media:" })
                    {
                        if (folderSelectionDialog.ShowDialog(this) == DialogResult.OK)
                        {
                            saveDir = folderSelectionDialog.SelectedPath;
                        }
                        else
                        {
                            return;
                        }
                    }
                }

                // Filter out types we can't process right now
                if (mResult.Type != MediaType.Album && mResult.Type != MediaType.Playlist &&
                    mResult.Type != MediaType.Track)
                {
                    using (var noTypeTd = CommonTaskDialogs.Message(owner: this, icon: TaskDialogStandardIcon.Warning,
                        caption: $"'{mResult.Type}' is not supported yet.",
                        message: "You may be able to download it in a later release."))
                    {
                        noTypeTd.Show();
                        return;
                    }
                }

                // Build wait dialog
                var retrievalWaitTaskDialog = new TaskDialog
                {
                    Cancelable = false,
                    Caption = "Athame",
                    InstructionText = $"Getting {mResult.Type.ToString().ToLower()} details...",
                    Text = $"{mService.Info.Name}: {mResult.Id}",
                    StandardButtons = TaskDialogStandardButtons.Cancel,
                    OwnerWindowHandle = Handle,
                    ProgressBar = new TaskDialogProgressBar { State = TaskDialogProgressBarState.Marquee }
                };
                // Open handler
                retrievalWaitTaskDialog.Opened += async (o, args) =>
                {
                    LockUi();
                    var pathFormat = Path.Combine(saveDir, prefType.GetPlatformSaveFormat());
                    try
                    {
                        switch (mResult.Type)
                        {
                            case MediaType.Album:
                                // Get album and display it in listview
                                var album = await mService.GetAlbumAsync(mResult.Id, true);
                                AddToQueue(mService, album, pathFormat);
                                break;

                            case MediaType.Playlist:
                                // Get playlist and display it in listview
                                var playlist = await mService.GetPlaylistAsync(mResult.Id);
                                if (playlist.Tracks == null)
                                {
                                    var items = mService.GetPlaylistItems(mResult.Id, 100);
                                    await items.LoadAllPagesAsync();
                                    playlist.Tracks = items.AllItems;
                                }
                                AddToQueue(mService, playlist, pathFormat);
                                break;

                            case MediaType.Track:
                                var track = await mService.GetTrackAsync(mResult.Id);
                                AddToQueue(mService, track.AsCollection(), pathFormat);
                                break;
                        }
                    }
                    catch (ResourceNotFoundException)
                    {
                        CommonTaskDialogs.Message(caption: "This media does not exist.",
    message: "Ensure the provided URL is valid, and try again", owner: this).Show();
                    }
                    catch (Exception ex)
                    {
                        CommonTaskDialogs.Exception(ex,
                            "An error occurred while trying to retrieve information for this media.",
                            "The provided URL may be invalid or unsupported.", this).Show();
                    }
                    idTextBox.Clear();
                    UnlockUi();
                    retrievalWaitTaskDialog.Close();
                };
                // Show dialog
                retrievalWaitTaskDialog.Show();
#if !DEBUG
        }
            catch (Exception ex)
            {
                PresentException(ex);
            }
#endif
        }

        private void RestoreFormPositionAndSize()
        {
            if (!IsWithinVisibleBounds(Program.DefaultSettings.Settings.MainWindowPreference.Location) ||
                Program.DefaultSettings.Settings.MainWindowPreference.Location == new Point(0, 0))
            {
                CenterToScreen();
            }
            else
            {
                Location = Program.DefaultSettings.Settings.MainWindowPreference.Location;
            }

            var savedSize = Program.DefaultSettings.Settings.MainWindowPreference.Size;
            if (savedSize.Width < MinimumSize.Width && savedSize.Height < MinimumSize.Height)
            {
                Program.DefaultSettings.Settings.MainWindowPreference.Size = savedSize = MinimumSize;
            }
            Size = savedSize;
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            RestoreFormPositionAndSize();
        }

        private void settingsButton_Click(object sender, EventArgs e)
        {
            var absLoc = settingsButton.PointToScreen(new Point(0, settingsButton.Height));
            mMenu.Show(absLoc);
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            Program.DefaultSettings.Save();
        }

        private void idTextBox_TextChanged(object sender, EventArgs e)
        {
            ValidateEnteredUrl();
        }

        private void clearButton_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            idTextBox.Clear();
        }

        private void pasteButton_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            idTextBox.Clear();
            idTextBox.Paste();
        }

        private void urlValidStateLabel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            var svc = (MusicService)e.Link.LinkData;
            using (var cf = new CredentialsForm(svc))
            {
                var res = cf.ShowDialog(this);
                if (res != DialogResult.OK) return;
                ValidateEnteredUrl();
            }

        }

        private async void StartDownload()
        {
            isListViewDirty = true;
            if (mediaDownloadQueue.Count == 0)
            {
                using (
                    var td = CommonTaskDialogs.Message(owner: this, icon: TaskDialogStandardIcon.Error, caption: "No tracks are in the queue.",
                        message: "You can add tracks by copying the URL to an album, artist, track, or playlist and pasting it into Athame."))
                {
                    td.Show();
                }
                return;
            }

            try
            {

                LockUi();
                totalStatusLabel.Text = "Warming up...";
                await mediaDownloadQueue.StartDownloadAsync();
                totalStatusLabel.Text = "All downloads completed";
                collectionStatusLabel.Text = GetCompletionMessage();
                currentlyDownloadingItem = null;
                mediaDownloadQueue.Clear();
                SetGlobalProgress(0);
                SystemSounds.Beep.Play();
                this.Flash(FlashMethod.All | FlashMethod.TimerNoForeground, Int32.MaxValue, 0);
            }
            catch (Exception ex)
            {
                PresentException(ex);

            }
            finally
            {
                UnlockUi();
            }
        }

        private async void startDownloadButton_Click(object sender, EventArgs e)
        {
            StartDownload();
        }

        private void queueListView_MouseClick(object sender, MouseEventArgs e)
        {
            if (!queueListView.FocusedItem.Bounds.Contains(e.Location)) return;
            mCurrentlySelectedQueueItem = queueListView.FocusedItem;
            // Only show context menu on right click
            if (e.Button != MouseButtons.Right) return;
            showCollectionInFileBrowserToolStripMenuItem.Enabled = GetCurrentlySelectedItemDir() != null;
            removeTrackToolStripMenuItem.Text = queueListView.SelectedItems.Count == 1 ? "Remove item" : "Remove items";
            queueMenu.Show(Cursor.Position);
        }



        private void removeGroupToolStripMenuItem_Click(object sender, EventArgs e)
        {
            RemoveCurrentlySelectedGroup();
        }

        private void queueListView_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (queueListView.SelectedIndices.Count == 0) return;
            mCurrentlySelectedQueueItem = queueListView.SelectedItems[0];
        }

        private void queueListView_MouseHover(object sender, EventArgs e)
        {

        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            new AboutForm().ShowDialog(this);
        }

        private void settingsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            new SettingsForm().ShowDialog(this);
        }



        private void removeTrackToolStripMenuItem_Click(object sender, EventArgs e)
        {
            RemoveCurrentlySelectedTracks();
        }

        private void MainForm_Move(object sender, EventArgs e)
        {
            Program.DefaultSettings.Settings.MainWindowPreference.Location = Location;
        }

        private void MainForm_ResizeEnd(object sender, EventArgs e)
        {
            Program.DefaultSettings.Settings.MainWindowPreference.Size = Size;
        }

        private void LoadAndInitPlugins()
        {
            Program.DefaultPluginManager.LoadAll();
            Program.DefaultPluginManager.InitAll();
            if (pluginLoadExceptions.Count > 0)
            {
                CommonTaskDialogs.Message("Plugin load error",
                    "One or more errors occurred while loading plugins. Some plugins may be unavailable. Check the log for more details.",
                    TaskDialogStandardButtons.Ok, TaskDialogStandardIcon.Warning, this).Show();
                pluginLoadExceptions.Clear();
            }
            if (!Program.DefaultPluginManager.AreAnyLoaded)
            {
#if DEBUG
                // https://youtu.be/Ki5cvEPu_e0?t=161
                var buttons = TaskDialogStandardButtons.Ok | TaskDialogStandardButtons.No;
#else
                var buttons = TaskDialogStandardButtons.Ok;
#endif
                if (CommonTaskDialogs.Message("No plugins installed",
                    "No plugins could be found. If you have attempted to install a plugin, it may not be installed properly.",
                    buttons, TaskDialogStandardIcon.Error, this).Show() != TaskDialogResult.No)
                {
                    Application.Exit();
                }
                
            }
        }

        private void MainForm_Shown(object sender, EventArgs e)
        {
            LockUi();
            LoadAndInitPlugins();
            RestoreServices();
            UnlockUi();
        }

        private void queueListView_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete)
            {
                if (e.Shift)
                {
                    RemoveCurrentlySelectedGroup();
                }
                else
                {
                    RemoveCurrentlySelectedTracks();
                }

            }
        }
#endregion

        private const int ImageListAnimStartIndex = 4;
        private const int ImageListAnimEndIndex = 15;
        private int currentFrame = ImageListAnimStartIndex;
        private ListViewItem currentAnimatingItem;

        private void queueImageAnimationTimer_Tick(object sender, EventArgs e)
        {
            currentFrame = ++currentFrame > ImageListAnimEndIndex ? ImageListAnimStartIndex : currentFrame;
            currentAnimatingItem.ImageIndex = currentFrame;
        }

        private void StartAnimation(ListViewItem item)
        {
            currentAnimatingItem = item;
            queueImageAnimationTimer.Start();
        }

        private void StopAnimation()
        {
            currentAnimatingItem = null;
            queueImageAnimationTimer.Stop();
        }

        private string GetCurrentlySelectedItemDir()
        {
            if (mCurrentlySelectedQueueItem == null) return null;
            var tag = (MediaItemTag)mCurrentlySelectedQueueItem.Tag;
            var parentDir = Path.GetDirectoryName(tag.Track.GetBasicPath(tag.Collection.PathFormat));
            return Directory.Exists(parentDir) ? parentDir : null;
        }

        private void showCollectionInFileBrowserToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var dir = GetCurrentlySelectedItemDir();
            if (dir == null) return;
            Process.Start($"\"{dir}\"");
        }

        private void ShowDetails()
        {
            var tag = (MediaItemTag) mCurrentlySelectedQueueItem?.Tag;
            if (tag?.Exception == null) return;
            using (var dialog = CommonTaskDialogs.Exception(tag.Exception, "An error occurred while downloading this track",
                "Check you can play this track on the web, check that you have a subscription, or try signing in and out.",
                this))
            {
                dialog.Show();
            }
        }

        private void queueListView_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            ShowDetails();
        }

        private void reportAnIssueToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }
    }
}
