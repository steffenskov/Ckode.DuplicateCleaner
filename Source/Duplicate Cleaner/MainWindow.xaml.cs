using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Duplicate_Cleaner
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public class CheckedListItem : INotifyPropertyChanged
        {
            public string Name { get; set; }
            public string Path { get; set; }
            public Guid Hash { get; set; }
            public bool IsAlternate { get; set; }
            public int Number { get; set; }

            public string KeepText => IsKept ? $@"Don't keep files in ""{Path}""" : $@"Keep files in ""{Path}""";

            private bool isChecked;
            public bool IsChecked
            {
                get => isChecked && !IsKept;
                set
                {
                    isChecked = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                }
            }

            private bool isKept;
            public bool IsKept
            {
                get => isKept;
                set
                {
                    isKept = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsKept)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
                }
            }

            public bool IsEnabled => !IsKept;

            public event PropertyChangedEventHandler PropertyChanged;
        }

        public ObservableCollection<CheckedListItem> Duplicates { get; set; }
        private readonly CancellationTokenSource _cancelSource = new CancellationTokenSource();
        private volatile bool _scanning = false;

        public MainWindow()
        {
            Duplicates = new ObservableCollection<CheckedListItem>();
            InitializeComponent();
            lstResult.ItemsSource = Duplicates;
        }

        private void btnSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                var result = dialog.ShowDialog();
                if (result == System.Windows.Forms.DialogResult.OK)
                {
                    tbPath.Text = dialog.SelectedPath;
                }
            }
        }

        private void btnScan_Click(object sender, RoutedEventArgs e)
        {
            if (_scanning)
            {
                btnScan.Content = "Cancelling...";
                btnScan.IsEnabled = false;
                _cancelSource.Cancel();
                return;
            }
            if (!Directory.Exists(tbPath.Text))
            {
                MessageBox.Show("The selected folder doesn't exist.", "Error");
                return;
            }

            pbFastScan.Value = pbFastScan.Minimum;
            pbSlowScan.Value = pbSlowScan.Minimum;
            btnScan.Content = "_Cancel";
            _scanning = true;

            Task.Factory.StartNew(ScanBody, Tuple.Create(_cancelSource.Token, tbPath.Text), _cancelSource.Token);
        }

        private void ScanBody(object state)
        {
            var args = (Tuple<CancellationToken, string>)state;
            var cancelToken = args.Item1;
            var rootPath = args.Item2;

            var rootDir = new DirectoryInfo(rootPath);
            var files = rootDir.GetFiles("*", SearchOption.AllDirectories);
            Dispatcher.Invoke(() => pbFastScan.Maximum = files.Length);

            var fastScanResult = FastScan(files, cancelToken); // MD5 hash comparison

            Dispatcher.Invoke(() => pbSlowScan.Maximum = fastScanResult.Values.Sum(list => list.Count));
            var slowScanResult = SlowScan(fastScanResult, cancelToken); // SHA1 hash comparison to ensure it's indeed duplicates

            _scanning = false;
            Dispatcher.Invoke(() =>
            {
                Duplicates.Clear();
                lblCount.Content = slowScanResult.Count + " duplicates";
                var isAlternate = false;
                var number = 1;
                foreach (var result in slowScanResult)
                {
                    var hash = result.Key;
                    foreach (var file in result.Value.OrderBy(file => file.DirectoryName).ThenBy(file => file.Name))
                    {
                        Duplicates.Add(new CheckedListItem
                        {
                            Number = number,
                            Hash = hash,
                            IsChecked = false,
                            Path = file.DirectoryName,
                            Name = file.Name,
                            IsAlternate = isAlternate
                        });
                    }
                    isAlternate = !isAlternate;
                    number++;
                }
                btnScan.Content = "_Find files";
                btnScan.IsEnabled = true;
                if (cancelToken.IsCancellationRequested)
                {
                    pbFastScan.Value = pbFastScan.Minimum;
                    pbSlowScan.Value = pbSlowScan.Minimum;
                }
            });
        }

        private IDictionary<Guid, IList<FileInfo>> FastScan(FileInfo[] files, CancellationToken cancelToken)
        {
            var duplicates = new Dictionary<Guid, IList<FileInfo>>();
            var @lock = new object();

            Parallel.ForEach(files, file =>
            {
                if (cancelToken.IsCancellationRequested)
                    return;
                var hash = GetMD5(file);

                lock (@lock)
                {
                    if (!duplicates.TryGetValue(hash, out var duplicateList))
                        duplicates[hash] = duplicateList = new List<FileInfo>();
                    duplicateList.Add(file);
                }

                Dispatcher.Invoke(() => pbFastScan.Value++);
            });
            if (!cancelToken.IsCancellationRequested)
            {
                RemoveSingles(duplicates);
            }
            return duplicates;
        }

        private IDictionary<Guid, IList<FileInfo>> SlowScan(IDictionary<Guid, IList<FileInfo>> foundDuplicates, CancellationToken cancelToken)
        {
            var duplicates = new Dictionary<Guid, IList<FileInfo>>();
            var @lock = new object();

            Parallel.ForEach(foundDuplicates, pair =>
            {
                if (cancelToken.IsCancellationRequested)
                    return;
                var md5 = pair.Key;
                var files = pair.Value;
                foreach (var file in files)
                {
                    var hash = GetSHA1(file);
                    lock (@lock)
                    {
                        if (!duplicates.TryGetValue(hash, out var duplicateList))
                            duplicates[hash] = duplicateList = new List<FileInfo>();
                        duplicateList.Add(file);
                    }
                    Dispatcher.Invoke(() =>
                    {
                        pbSlowScan.Value++;
                    });
                }
            });

            if (!cancelToken.IsCancellationRequested)
            {
                RemoveSingles(duplicates);
            }
            return duplicates;
        }

        private void RemoveSingles<T>(IDictionary<Guid, IList<T>> duplicates)
        {
            var hashesToRemove = duplicates
                                          .Where(pair => pair.Value.Count == 1)
                                          .Select(pair => pair.Key)
                                          .ToList();
            foreach (var hash in hashesToRemove)
            {
                duplicates.Remove(hash);
            }
        }

        private Guid GetMD5(FileInfo file)
        {
            using (var md5 = MD5.Create())
            using (var fileStream = file.OpenRead())
            {
                return new Guid(md5.ComputeHash(fileStream));
            }
        }

        private Guid GetSHA1(FileInfo file)
        {
            using (var sha1 = SHA1.Create())
            using (var fileStream = file.OpenRead())
            {
                var hash = sha1.ComputeHash(fileStream);
                var bt = new byte[16];
                Array.Copy(hash, bt, 16);
                return new Guid(bt); // HACK: Toss away the last 4 bytes of the SHA1, we only need to verify the files that match on MD5 also seemingly match on SHA1. This is unlikely to ever occur where only the last 4 bytes is a mismatch.
            }
        }

        private void lstResult_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0)
            {
                var item = e.AddedItems[0] as CheckedListItem;
                if (item.IsKept)
                {
                    item.IsChecked = false; // Cannot check a kept item
                }
                else
                {
                    if (!item.IsChecked) // Sanity check: Protect against deleting ALL versions of a duplicate
                    {
                        var duplicatesToCheck = Duplicates
                                                    .Where(elm => elm.Hash == item.Hash && elm != item)
                                                    .ToList();
                        if (duplicatesToCheck.All(elm => elm.IsChecked))
                        {
                            MessageBox.Show("You cannot mark every version of a duplicate for deletion.", "Error");
                        }
                        else
                            item.IsChecked = !item.IsChecked;
                    }
                    else
                        item.IsChecked = !item.IsChecked;
                }

                lstResult.UnselectAll();
                lstResult.UnselectAllCells();
            }
        }

        private void miKeep_Click(object sender, RoutedEventArgs e)
        {
            var mi = sender as MenuItem;
            var path = (string)mi.Tag;

            var grouped = Duplicates.GroupBy(item => item.Hash)
                                    .ToDictionary(grp => grp.Key, grp => grp.ToList());

            Parallel.ForEach(grouped, grp =>
            {
                var itemClicked = grp.Value.FirstOrDefault(item => item.Path == path);

                if (itemClicked != null)
                {
                    if (itemClicked.IsKept)
                    {
                        var itemToKeep = grp.Value.FirstOrDefault(item => item != itemClicked && item.IsKept);
                        if (itemToKeep != null)
                        {
                            itemClicked.IsKept = false;
                            itemClicked.IsChecked = true;
                            return;
                        }
                    }
                    foreach (var item in grp.Value)
                    {
                        if (item != itemClicked)
                            item.IsChecked = !itemClicked.IsKept;
                    }
                    itemClicked.IsKept = !itemClicked.IsKept;
                    itemClicked.IsChecked = false;
                }
            });
        }

        private void btnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("Are you certain you want to delete the marked files?", "Delete confirmation", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;

            var pathsRemoved = new HashSet<string>();
            var removedItems = new List<CheckedListItem>();
            foreach (var item in Duplicates)
            {
                if (item.IsChecked)
                {
                    try
                    {
                        File.Delete(Path.Combine(item.Path, item.Name));
                        removedItems.Add(item);
                        pathsRemoved.Add(item.Path);
                    }
                    catch
                    {
                    }
                }
            }
            foreach (var item in removedItems)
            {
                Duplicates.Remove(item);
            }
            var singlesToRemove = Duplicates
                                        .GroupBy(item => item.Hash)
                                        .ToDictionary(grp => grp.Key, grp => grp.ToList())
                                        .Where(grp => grp.Value.Count <= 1)
                                        .SelectMany(grp => grp.Value);

            foreach (var itemToRemove in singlesToRemove)
            {
                Duplicates.Remove(itemToRemove);
            }
            if (cbDeleteEmptyDirectories.IsChecked == true)
            {
                foreach (var path in pathsRemoved)
                {
                    var dir = new DirectoryInfo(path);
                    while (dir != null && dir.Exists && IsDirectoryEmpty(dir.FullName) && dir.Parent != null)
                    {
                        var parent = dir.Parent;
                        dir.Delete();
                        dir = parent;
                    }
                }
            }

            if (Duplicates.Count > 0)
            {
                var number = 1;
                var isAlternate = false;
                Guid prevHash = Duplicates[0].Hash;
                foreach (var item in Duplicates)
                {
                    if (item.Hash != prevHash)
                    {
                        number++;
                        isAlternate = !isAlternate;
                        prevHash = item.Hash;
                    }
                    item.Number = number;
                    item.IsAlternate = isAlternate;
                }
            }
            lblCount.Content = Duplicates.Count + " duplicates";
        }

        public bool IsDirectoryEmpty(string path)
        {
            return !Directory.EnumerateFileSystemEntries(path).Any();
        }
    }
}
