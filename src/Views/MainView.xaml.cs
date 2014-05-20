﻿/*  
 * Papercut
 *
 *  Copyright © 2008 - 2012 Ken Robertson
 *  Copyright © 2013 - 2014 Jaben Cargman
 *  
 *  Licensed under the Apache License, Version 2.0 (the "License");
 *  you may not use this file except in compliance with the License.
 *  You may obtain a copy of the License at
 *  
 *  http://www.apache.org/licenses/LICENSE-2.0
 *  
 *  Unless required by applicable law or agreed to in writing, software
 *  distributed under the License is distributed on an "AS IS" BASIS,
 *  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *  See the License for the specific language governing permissions and
 *  limitations under the License.
 *  
 */

namespace Papercut.Views
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Drawing;
    using System.IO;
    using System.Linq;
    using System.Reactive.Linq;
    using System.Reflection;
    using System.Text;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Forms;
    using System.Windows.Input;
    using System.Windows.Media;
    using System.Windows.Navigation;

    using Autofac;

    using Caliburn.Micro;

    using MahApps.Metro.Controls;

    using MimeKit;

    using Papercut.Core;
    using Papercut.Core.Events;
    using Papercut.Core.Helper;
    using Papercut.Core.Message;
    using Papercut.Events;
    using Papercut.Helpers;
    using Papercut.Properties;
    using Papercut.Services;
    using Papercut.ViewModels;

    using Serilog;

    using Action = System.Action;
    using Application = System.Windows.Application;
    using ContextMenu = System.Windows.Forms.ContextMenu;
    using DataFormats = System.Windows.DataFormats;
    using DataObject = System.Windows.DataObject;
    using DragDropEffects = System.Windows.DragDropEffects;
    using KeyEventArgs = System.Windows.Input.KeyEventArgs;
    using ListBox = System.Windows.Controls.ListBox;
    using MenuItem = System.Windows.Forms.MenuItem;
    using MessageBox = System.Windows.MessageBox;
    using MouseEventArgs = System.Windows.Input.MouseEventArgs;
    using Point = System.Windows.Point;
    using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;

    /// <summary>
    ///     Interaction logic for MainView.xaml
    /// </summary>
    public partial class MainView : MetroWindow, IHandle<ShowMainWindowEvent>, IHandle<SmtpServerBindFailedEvent>, IHandle<ShowMessageEvent>
    {
        readonly Func<OptionsViewModel> _optionsViewModelFactory;

        #region Fields

        readonly object _deleteLockObject = new object();

        Point? _dragStartPoint;

        IDisposable _loadingDisposable;

        public ILogger Logger { get; set; }

        public MimeMessageLoader MimeMessageLoader { get; set; }

        public AppResourceLocator ResourceLocator { get; set; }

        public IWindowManager WindowManager { get; set; }

        public IPublishEvent PublishEvent { get; set; }

        public MessageRepository MessageRepository { get; set; }

        #endregion

        #region Constructors and Destructors

        public MainView(
            MessageRepository messageRepository,
            MimeMessageLoader mimeMessageLoader,
            AppResourceLocator resourceLocator,
            Func<OptionsViewModel> optionsViewModelFactory,
            IWindowManager windowManager,
            IPublishEvent publishEvent,
            ILogger logger)
        {
            _optionsViewModelFactory = optionsViewModelFactory;
            MessageRepository = messageRepository;
            MimeMessageLoader = mimeMessageLoader;
            ResourceLocator = resourceLocator;
            WindowManager = windowManager;
            PublishEvent = publishEvent;
            Logger = logger;

            InitializeComponent();

            // Begin listening for new messages
            MessageRepository.NewMessage += NewMessage;
            MessageRepository.RefreshNeeded += RefreshMessages;

            // Set the version label
            versionLabel.Content = string.Format(
                "Papercut v{0}",
                Assembly.GetExecutingAssembly().GetName().Version.ToString(3));

            // Load existing messages
            RefreshMessageList();

            // Minimize if set to
            if (Settings.Default.StartMinimized) Hide();
        }

        #endregion

        #region Methods

        protected override void OnStateChanged(EventArgs e)
        {
            // Hide the window if minimized so it doesn't show up on the task bar
            if (WindowState == WindowState.Minimized) Hide();

            base.OnStateChanged(e);
        }

        /// <summary>
        ///     Add a newly received message and show the balloon notification
        /// </summary>
        /// <param name="entry">
        ///     The entry.
        /// </param>
        void AddNewMessage(MessageEntry entry)
        {
            MimeMessageLoader.Get(entry).ObserveOnDispatcher().Subscribe(
                message =>
                {
                    PublishEvent.Publish(
                        new ShowBallonTip(
                            5000,
                            "New Message Received",
                            string.Format(
                                "From: {0}\r\nSubject: {1}",
                                message.From.ToString().Truncate(50),
                                message.Subject.Truncate(50)),
                            ToolTipIcon.Info));

                    // Add it to the list box
                    messagesList.Items.Add(entry);
                });
        }

        void DeleteSelectedMessage()
        {
            // Lock to prevent rapid clicking issues
            lock (_deleteLockObject)
            {
                var messages = new MessageEntry[messagesList.SelectedItems.Count];
                messagesList.SelectedItems.CopyTo(messages, 0);

                // Capture index position first
                int index = messagesList.SelectedIndex;

                foreach (MessageEntry entry in messages)
                {
                    MessageRepository.DeleteMessage(entry);
                    messagesList.Items.Remove(entry);
                }

                UpdateSelectedMessage(index);
            }
        }

        void DisplayMimeMessage(MimeMessage mailMessageEx)
        {
            headerView.Text = string.Join("\r\n", mailMessageEx.Headers.Select(h => h.ToString()));

            List<MimePart> parts = mailMessageEx.BodyParts.ToList();
            TextPart mainBody = parts.GetMainBodyTextPart();

            bodyView.Text = mainBody.Text;
            bodyViewTab.Visibility = Visibility.Visible;

            defaultBodyView.Text = mainBody.Text;

            FromEdit.Text = mailMessageEx.From.IfNotNull(s => s.ToString()) ?? string.Empty;
            ToEdit.Text = mailMessageEx.To.IfNotNull(s => s.ToString()) ?? string.Empty;
            CCEdit.Text = mailMessageEx.Cc.IfNotNull(s => s.ToString()) ?? string.Empty;
            BccEdit.Text = mailMessageEx.Bcc.IfNotNull(s => s.ToString()) ?? string.Empty;
            DateEdit.Text = mailMessageEx.Date.IfNotNull(s => s.ToString()) ?? string.Empty;

            string subject = mailMessageEx.Subject ?? string.Empty;
            SubjectEdit.Text = subject;

            SetWindowTitle(subject);

            bool isContentHtml = mainBody.IsContentHtml();
            textViewTab.Visibility = Visibility.Hidden;

            if (isContentHtml)
            {
                SetBrowserDocument(mailMessageEx);

                TextPart textPartNotHtml =
                    parts.OfType<TextPart>().Except(new[] { mainBody }).FirstOrDefault();
                if (textPartNotHtml != null)
                {
                    textViewTab.Visibility = Visibility.Visible;
                    textView.Text = textPartNotHtml.Text;

                    if (Equals(tabControl.SelectedItem, textViewTab)) tabControl.SelectedIndex = 2;
                }
            }

            if (defaultTab.IsVisible) tabControl.SelectedIndex = 0;

            defaultHtmlView.Visibility = isContentHtml ? Visibility.Visible : Visibility.Collapsed;
            defaultBodyView.Visibility = isContentHtml ? Visibility.Collapsed : Visibility.Visible;

            SpinAnimation.Visibility = Visibility.Collapsed;
            tabControl.IsEnabled = true;

            // Enable the delete and forward button
            deleteButton.IsEnabled = true;
            forwardButton.IsEnabled = true;
        }

        void Exit_Click(object sender, RoutedEventArgs e)
        {
            PublishEvent.Publish(new AppForceShutdownEvent());
        }

        void GoToSite(object sender, MouseButtonEventArgs e)
        {
            Process.Start("http://papercut.codeplex.com/");
        }

        /// <summary>
        ///     Handles the OnKeyDown event of the MessagesList control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="KeyEventArgs" /> instance containing the event data.</param>
        void MessagesList_OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete) return;

            DeleteSelectedMessage();
        }

        void MessagesList_OnPreviewLeftMouseDown(object sender, MouseButtonEventArgs e)
        {
            var parent = sender as ListBox;

            if (parent == null) return;

            if (_dragStartPoint == null) _dragStartPoint = e.GetPosition(parent);
        }

        static T FindAncestor<T>(DependencyObject dependencyObject) where T : DependencyObject
        {
            DependencyObject parent = VisualTreeHelper.GetParent(dependencyObject);
            if (parent == null) return null;
            var parentT = parent as T;
            return parentT ?? FindAncestor<T>(parent);
        }

        void MessagesList_OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            var parent = sender as ListBox;

            if (parent == null || _dragStartPoint == null) return;
            if (FindAncestor<ScrollBar>((DependencyObject)e.OriginalSource) != null) return;

            Point dragPoint = e.GetPosition(parent);

            Vector potentialDragLength = dragPoint - _dragStartPoint.Value;

            if (potentialDragLength.Length > 10)
            {
                // Get the object source for the selected item
                var entry = parent.GetObjectDataFromPoint<MessageEntry>(_dragStartPoint.Value);

                // If the data is not null then start the drag drop operation
                if (entry != null && !string.IsNullOrWhiteSpace(entry.File))
                {
                    var dataObject = new DataObject(DataFormats.FileDrop, new[] { entry.File });
                    DragDrop.DoDragDrop(parent, dataObject, DragDropEffects.Copy);
                }

                _dragStartPoint = null;
            }
        }

        void MessagesList_OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = null;
        }

        void NewMessage(object sender, NewMessageEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => AddNewMessage(e.NewMessage)));
        }

        void Options_Click(object sender, RoutedEventArgs e)
        {
            WindowManager.ShowDialog(_optionsViewModelFactory());

            //var ow = new OptionsView(PublishEvent) { Owner = this, ShowInTaskbar = false };

            //ow.ShowDialog();
        }

        void RefreshMessageList()
        {
            IList<MessageEntry> messageEntries =
                PapercutContainer.Instance.Resolve<MessageRepository>().LoadMessages();

            messagesList.Items.Clear();

            foreach (MessageEntry messageEntry in messageEntries)
            {
                messagesList.Items.Add(messageEntry);
            }

            messagesList.Items.SortDescriptions.Add(
                new SortDescription("ModifiedDate", ListSortDirection.Ascending));

            UpdateSelectedMessage();
        }

        void RefreshMessages(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(RefreshMessageList));
        }

        /// <summary>
        ///     WriteFormat the HTML to a temporary file and render it to the HTML view
        /// </summary>
        /// <param name="mailMessageEx">
        ///     The mail Message Ex.
        /// </param>
        void SetBrowserDocument(MimeMessage mailMessageEx)
        {
            Observable.Start(
                () =>
                {
                    try
                    {
                        return SaveBrowserTempHtmlFile(mailMessageEx);
                    }
                    catch (Exception ex)
                    {
                        Log.Logger.Error(
                            ex,
                            "Exception Saving Browser Temp File for {MailMessage}",
                            mailMessageEx.ToString());
                    }

                    return null;
                }).ObserveOnDispatcher().Where(s => !string.IsNullOrEmpty(s)).Subscribe(
                    h =>
                    {
                        defaultHtmlView.NavigationUIVisibility = NavigationUIVisibility.Hidden;
                        defaultHtmlView.Navigate(new Uri(h));
                        defaultHtmlView.Refresh();
                    });
        }

        static string SaveBrowserTempHtmlFile(MimeMessage mailMessageEx)
        {
            var replaceEmbeddedImageFormats = new[] { @"cid:{0}", @"cid:'{0}'", @"cid:""{0}""" };

            string tempPath = Path.GetTempPath();
            string htmlFile = Path.Combine(tempPath, "papercut.htm");

            string htmlText = mailMessageEx.BodyParts.GetMainBodyTextPart().Text;

            foreach (
                MimePart image in
                    mailMessageEx.GetImages().Where(i => !string.IsNullOrWhiteSpace(i.ContentId)))
            {
                string fileName = Path.Combine(tempPath, image.ContentId);
                using (FileStream fs = File.OpenWrite(fileName))
                {
                    using (Stream content = image.ContentObject.Open()) content.CopyBufferedTo(fs);
                    fs.Close();
                }

                htmlText = replaceEmbeddedImageFormats.Aggregate(
                    htmlText,
                    (current, format) =>
                    current.Replace(string.Format(format, image.ContentId), image.ContentId));
            }

            File.WriteAllText(htmlFile, htmlText, Encoding.Unicode);

            return htmlFile;
        }

        void SetWindowTitle(string title)
        {
            Subject.Content = title;
            Subject.ToolTip = title;
        }

        void UpdateSelectedMessage(int? index = null)
        {
            // If there are more than the index location, keep the same position in the list
            if (index.HasValue && messagesList.Items.Count > index) messagesList.SelectedIndex = index.Value;
            else if (messagesList.Items.Count > 0)
            {
                // If there are fewer, move to the last one
                messagesList.SelectedIndex = messagesList.Items.Count - 1;
            }
            else if (messagesList.Items.Count == 0) tabControl.IsEnabled = false;
        }

        void Window_Closing(object sender, CancelEventArgs e)
        {
            if (Application.Current.ShutdownMode == ShutdownMode.OnExplicitShutdown)
            {
                return;
            }

            //Cancel close and minimize if setting is set to minimize on close
            if (Settings.Default.MinimizeOnClose)
            {
                e.Cancel = true;
                WindowState = WindowState.Minimized;
            }
        }

        void deleteButton_Click(object sender, RoutedEventArgs e)
        {
            DeleteSelectedMessage();
        }

        void forwardButton_Click(object sender, RoutedEventArgs e)
        {
            var entry = messagesList.SelectedItem as MessageEntry;
            if (entry != null)
            {
                var fw = new ForwardView(MessageRepository) { Owner = this, MessageEntry = entry };
                fw.ShowDialog();
            }
        }

        void messagesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // If there are no selected items, then disable the Delete button, clear the boxes, and return
            if (e.AddedItems.Count == 0)
            {
                deleteButton.IsEnabled = false;
                forwardButton.IsEnabled = false;
                headerView.Text = string.Empty;
                bodyView.Text = string.Empty;
                textViewTab.Visibility = Visibility.Hidden;
                tabControl.SelectedIndex = defaultTab.IsVisible ? 0 : 1;

                // Clear fields
                FromEdit.Text = string.Empty;
                ToEdit.Text = string.Empty;
                CCEdit.Text = string.Empty;
                BccEdit.Text = string.Empty;
                DateEdit.Text = string.Empty;

                string subject = string.Empty;
                SubjectEdit.Text = subject;

                defaultBodyView.Text = string.Empty;

                defaultHtmlView.Content = null;
                defaultHtmlView.NavigationService.RemoveBackEntry();
                //this.defaultHtmlView.Refresh();

                SetWindowTitle("Papercut");

                return;
            }

            var messageEntry = ((MessageEntry)e.AddedItems[0]);

            try
            {
                tabControl.IsEnabled = false;
                SpinAnimation.Visibility = Visibility.Visible;

                SetWindowTitle("Loading...");

                if (_loadingDisposable != null) _loadingDisposable.Dispose();

                // show it...
                _loadingDisposable =
                    MimeMessageLoader.Get(messageEntry)
                        .ObserveOnDispatcher()
                        .Subscribe(DisplayMimeMessage);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, @"Unable to Load Message ""{0}"": {1}", messageEntry.File);
                SetWindowTitle("Papercut");
                tabControl.SelectedIndex = 1;
                bodyViewTab.Visibility = Visibility.Hidden;
                textViewTab.Visibility = Visibility.Hidden;
            }
        }

        #endregion

        void IHandle<ShowMainWindowEvent>.Handle(ShowMainWindowEvent message)
        {
            Show();
            WindowState = WindowState.Normal;
            Topmost = true;
            Focus();
            Topmost = false;

            if (message.SelectMostRecentMessage)
            {
                messagesList.SelectedIndex = messagesList.Items.Count - 1;
            }
        }

        void IHandle<SmtpServerBindFailedEvent>.Handle(SmtpServerBindFailedEvent message)
        {
            MessageBox.Show(
                "Failed to start SMTP server listening. The IP and Port combination is in use by another program. To fix, change the server bindings in the options.",
                "Failed");

            Options_Click(null, null);
        }

        void IHandle<ShowMessageEvent>.Handle(ShowMessageEvent message)
        {
            MessageBox.Show(message.MessageText, message.Caption);
        }
    }
}