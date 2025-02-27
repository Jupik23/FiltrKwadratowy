// Temat projektu: Przetwarzanie obrazów w aplikacji WPF z wykorzystaniem algorytmów w C++ i ASM
// Opis algorytmu: Interfejs aplikacji, który pozwala na wybór obrazu, zastosowanie filtra 5x5 oraz zapisanie wynikowego obrazu.
// Data wykonania projektu: Semestr Zimowy 2024/2025
// Autor: Mateusz Skrzypiec

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Windows.Shapes;

namespace JAApp
{
    public partial class MainWindow : Window
    {
        // Importowanie funkcji z bibliotek DLL:
        // - `ApplyASMFilter` to funkcja wykorzystująca algorytm w ASM do przetwarzania obrazu.
        // - `ApplyCFilter` to funkcja wykorzystująca algorytm w C++ do przetwarzania obrazu.
        [DllImport("JADll.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int ApplyASMFilter(IntPtr pixelData, int width, int startY, int endY, int imageHeight);

        [DllImport("CPPDll.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int ApplyCFilter(IntPtr pixelData, int width, int startY, int endY, int imageHeight);

        // Ścieżka wybranego pliku obrazu.
        private string selectedFilePath;

        // Tablica bajtów reprezentująca piksele obrazu w formacie BGR.
        private byte[] imagePixels;

        // Szerokość obrazu w pikselach.
        private int imageWidth;

        // Wysokość obrazu w pikselach.
        private int imageHeight;

        // Liczba wybranych wątków do przetwarzania obrazu.
        private int selectedThreads;

        // Flaga określająca, czy algorytm został zastosowany.
        private bool isAlgorithmApplied = false;

        // Konstruktor klasy MainWindow.
        public MainWindow()
        {
            InitializeComponent(); // Inicjalizacja komponentów WPF.
            InitializeThreadSelection(); // Inicjalizacja opcji wyboru liczby wątków.
        }

        // Inicjalizuje opcje wyboru liczby wątków w interfejsie użytkownika.
        private void InitializeThreadSelection()
        {
            int realThreads = Environment.ProcessorCount; // Pobranie liczby dostępnych wątków procesora.
            selectedThreads = realThreads; // Domyślnie ustawienie na maksymalną liczbę wątków.

            // Wypełnienie listy wyboru liczbą wątków od 1 do 64.
            ThreadsComboBox.ItemsSource = Enumerable.Range(1, 64).ToList();
            ThreadsComboBox.SelectedItem = realThreads; // Wybranie domyślnej liczby wątków.
            ThreadsComboBox.SelectionChanged += (s, e) =>
            {
                // Aktualizacja liczby wątków po zmianie w interfejsie.
                if (ThreadsComboBox.SelectedItem is int selected)
                {
                    selectedThreads = selected;
                    Debug.WriteLine($"Selected Threads: {selectedThreads}"); // Debugowanie wybranej liczby wątków.
                }
            };
        }

        // Obsługa przycisku wyboru pliku obrazu.
        private void ChooseFileButton_Click(object sender, RoutedEventArgs e)
        {
            // Okno dialogowe do wyboru pliku obrazu.
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Title = "Wybierz obraz",
                Filter = "Pliki graficzne (*.jpg;*.png;*.bmp)|*.jpg;*.png;*.bmp"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                // Zapisanie ścieżki wybranego pliku.
                selectedFilePath = openFileDialog.FileName;

                try
                {
                    // Sprawdzanie rozmiaru pliku.
                    var fileInfo = new System.IO.FileInfo(selectedFilePath);
                    long maxSizeInBytes = 100 * 1024 * 1024; // Maksymalny rozmiar pliku (100 MB).

                    if (fileInfo.Length > maxSizeInBytes)
                    {
                        MessageBox.Show("Wybrany plik jest zbyt duży! Maksymalny obsługiwany rozmiar to 100 MB.",
                            "Zbyt duży plik", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return; // Zatrzymanie dalszego przetwarzania.
                    }

                    LoadToGui(selectedFilePath); // Załadowanie obrazu do podglądu GUI.
                    ConvertToBitMap(selectedFilePath); // Konwersja obrazu na format bitmapy.
                    DrawHistogram(HistogramCanvas1, CalculateHistogram(imagePixels)); // Rysowanie histogramu dla wybranego obrazu.
                }
                catch (Exception ex)
                {
                    // Obsługa błędu podczas ładowania obrazu.
                    MessageBox.Show($"Błąd podczas ładowania obrazu: {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }


        // Ładuje obraz do elementu GUI do podglądu.
        private void LoadToGui(string path)
        {
            DisplayImage1.Source = new BitmapImage(new Uri(path)); // Ustawienie obrazu jako źródło wyświetlacza.
        }

        // Konwertuje obraz z wybranej ścieżki na tablicę pikseli.
        private void ConvertToBitMap(string path)
        {
            BitmapImage bitmapImage = new BitmapImage(new Uri(path)); // Wczytanie obrazu.

            // Pobranie szerokości i wysokości obrazu.
            imageWidth = bitmapImage.PixelWidth;
            imageHeight = bitmapImage.PixelHeight;

            // Obliczenie rozmiaru wiersza w bajtach (RGB = 3 bajty na piksel).
            int stride = imageWidth * 3;
            imagePixels = new byte[imageHeight * stride]; // Utworzenie tablicy pikseli.
            FormatConvertedBitmap formattedBitmap = new FormatConvertedBitmap(bitmapImage, PixelFormats.Rgb24, null, 0);
            formattedBitmap.CopyPixels(imagePixels, stride, 0); // Skopiowanie pikseli do tablicy.

            Debug.WriteLine($"Image loaded: {imageWidth}x{imageHeight}, Pixels extracted: {imagePixels.Length}"); // Informacja debugująca o obrazie.
        }

        // Konwertuje przetworzoną tablicę pikseli na obraz i wyświetla go w interfejsie użytkownika.
        private void ConvertToImage()
        {
            if (imagePixels == null)
            {
                // Ostrzeżenie w przypadku braku danych pikseli.
                MessageBox.Show("Brak danych pikseli do konwersji!", "Błąd", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // Utworzenie obrazu z przetworzonych pikseli.
                WriteableBitmap bitmap = new WriteableBitmap(imageWidth, imageHeight, 96, 96, PixelFormats.Rgb24, null);
                bitmap.WritePixels(new Int32Rect(0, 0, imageWidth, imageHeight), imagePixels, imageWidth * 3, 0);
                DisplayImage2.Source = bitmap; // Ustawienie obrazu w podglądzie przetworzonego obrazu.
                DrawHistogram(HistogramCanvas2, CalculateHistogram(imagePixels)); // Rysowanie histogramu przetworzonego obrazu.
                Debug.WriteLine("Processed image displayed successfully."); // Informacja debugująca.
            }
            catch (Exception ex)
            {
                // Obsługa błędu podczas konwersji obrazu.
                MessageBox.Show($"Błąd podczas konwersji obrazu: {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Zastosowanie filtru (C++ lub ASM) przy użyciu wielu wątków.
        private void ApplyFilterWithThreads(Func<IntPtr, int, int, int, int, int> filterFunction)
        {
            if (string.IsNullOrEmpty(selectedFilePath))
            {
                // Ostrzeżenie w przypadku braku wybranego obrazu.
                MessageBox.Show("Najpierw wybierz i załaduj obraz!", "Błąd", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                ConvertToBitMap(selectedFilePath); // Konwersja obrazu do formatu tablicy pikseli.

                // Obliczenie podziału obrazu na segmenty dla wątków.
                int baseSegmentHeight = imageHeight / selectedThreads; // Wysokość bazowego segmentu.
                int extraRows = imageHeight % selectedThreads; // Dodatkowe wiersze dla nierównomiernych podziałów.

                // Tablice przechowujące zakresy wierszy dla każdego wątku.
                int[] startYs = new int[selectedThreads];
                int[] endYs = new int[selectedThreads];

                // Wyznaczanie zakresów wierszy dla poszczególnych wątków.
                int currentStartY = 0;
                for (int i = 0; i < selectedThreads; i++)
                {
                    int segmentHeight = baseSegmentHeight + (i < extraRows ? 1 : 0); // Ustalanie wysokości segmentu.
                    startYs[i] = currentStartY;
                    endYs[i] = currentStartY + segmentHeight;
                    currentStartY += segmentHeight;
                }

                // Przypięcie tablicy pikseli do pamięci, by uniknąć problemów z Garbage Collector.
                GCHandle handle = GCHandle.Alloc(imagePixels, GCHandleType.Pinned);
                IntPtr pixelDataPtr = Marshal.UnsafeAddrOfPinnedArrayElement(imagePixels, 0);

                // Rozpoczęcie pomiaru czasu.
                Stopwatch stopwatch = Stopwatch.StartNew();
                Parallel.For(0, selectedThreads, threadIndex =>
                {
                    // Wywołanie funkcji filtra dla przypisanego zakresu wątków.
                    filterFunction(pixelDataPtr, imageWidth, startYs[threadIndex], endYs[threadIndex], imageHeight);
                });

                stopwatch.Stop(); // Zakończenie pomiaru czasu.
                handle.Free(); // Zwolnienie uchwytu pamięci.

                ConvertToImage(); // Konwersja i wyświetlenie przetworzonego obrazu.
                isAlgorithmApplied = true; // Ustawienie flagi oznaczającej zastosowanie algorytmu.
                MessageBox.Show($"Filtr został zastosowany w {stopwatch.ElapsedMilliseconds} ms", "Sukces", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                // Obsługa błędów podczas zastosowania filtra.
                MessageBox.Show($"Błąd podczas wywołania filtru: {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Funkcja obsługująca przycisk wywołania filtra C++.
        private void cButton(object sender, RoutedEventArgs e)
        {
            ApplyFilterWithThreads(ApplyCFilter); // Wywołanie filtra w C++.
        }

        // Funkcja obsługująca przycisk wywołania filtra ASM.
        private void asmButton(object sender, RoutedEventArgs e)
        {
            ApplyFilterWithThreads(ApplyASMFilter); // Wywołanie filtra w ASM.
        }

        // Oblicza histogram obrazu na podstawie pikseli.
        private int[][] CalculateHistogram(byte[] pixels)
        {
            int[][] histogram = new int[3][]; // Histogram dla trzech kanałów: Blue, Green, Red.
            histogram[0] = new int[256]; // Kanał Blue.
            histogram[1] = new int[256]; // Kanał Green.
            histogram[2] = new int[256]; // Kanał Red.

            // Iteracja po pikselach obrazu (3 bajty na piksel: B, G, R).
            for (int i = 0; i < pixels.Length; i += 3)
            {
                histogram[0][pixels[i]]++;       // Blue
                histogram[1][pixels[i + 1]]++;  // Green
                histogram[2][pixels[i + 2]]++;  // Red
            }

            return histogram; // Zwrócenie obliczonego histogramu.
        }

        // Obsługuje zapis przetworzonego obrazu do pliku.
        private void SaveFileButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isAlgorithmApplied)
            {
                // Ostrzeżenie w przypadku braku zastosowanego algorytmu.
                MessageBox.Show("Najpierw zastosuj algorytm przed zapisaniem obrazu!", "Błąd", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (imagePixels == null || string.IsNullOrEmpty(selectedFilePath))
            {
                // Ostrzeżenie w przypadku braku danych do zapisania.
                MessageBox.Show("Najpierw przetwórz obraz przed zapisem!", "Błąd", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Okno dialogowe do wyboru lokalizacji i formatu zapisu pliku.
            SaveFileDialog saveFileDialog = new SaveFileDialog
            {
                Title = "Zapisz obraz",
                Filter = "Pliki BMP (*.bmp)|*.bmp|Pliki JPEG (*.jpg)|*.jpg|Pliki PNG (*.png)|*.png",
                FileName = System.IO.Path.GetFileNameWithoutExtension(selectedFilePath) + "_processed"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                string savePath = saveFileDialog.FileName; // Pobranie ścieżki wybranego pliku.

                if (string.Equals(savePath, selectedFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    // Ostrzeżenie w przypadku próby nadpisania oryginalnego obrazu.
                    MessageBox.Show("Nie można nadpisać oryginalnego obrazu w użyciu!", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                string extension = System.IO.Path.GetExtension(savePath).ToLower();

                // Sprawdzenie obsługiwanego formatu zapisu.
                if (extension != ".bmp" && extension != ".jpg" && extension != ".jpeg" && extension != ".png")
                {
                    MessageBox.Show("Nieobsługiwany format pliku!", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                try
                {
                    // Zapis obrazu w wybranym formacie.
                    SaveImage(savePath, extension.TrimStart('.'));
                    MessageBox.Show($"Obraz zapisany pomyślnie: {savePath}", "Sukces", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    // Obsługa błędów podczas zapisu obrazu.
                    MessageBox.Show($"Błąd podczas zapisywania obrazu: {ex.Message}", "Błąd", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // Zapisuje obraz do pliku w wybranym formacie.
        private void SaveImage(string filePath, string format)
        {
            if (imagePixels == null)
            {
                throw new InvalidOperationException("Brak danych pikseli do zapisania.");
            }

            // Tworzenie WriteableBitmap z danych pikseli.
            WriteableBitmap bitmap = new WriteableBitmap(imageWidth, imageHeight, 96, 96, PixelFormats.Rgb24, null);
            bitmap.WritePixels(new Int32Rect(0, 0, imageWidth, imageHeight), imagePixels, imageWidth * 3, 0);

            // Wybór odpowiedniego enkodera na podstawie formatu.
            BitmapEncoder encoder;
            switch (format.ToLower())
            {
                case "bmp":
                    encoder = new BmpBitmapEncoder();
                    break;
                case "jpg":
                case "jpeg":
                    encoder = new JpegBitmapEncoder();
                    break;
                case "png":
                    encoder = new PngBitmapEncoder();
                    break;
                default:
                    throw new ArgumentException("Nieobsługiwany format zapisu: " + format);
            }

            // Zapis obrazu do pliku za pomocą wybranego enkodera.
            using (var fileStream = new System.IO.FileStream(filePath, System.IO.FileMode.Create))
            {
                encoder.Frames.Add(BitmapFrame.Create(bitmap)); // Dodanie klatki bitmapy do enkodera.
                encoder.Save(fileStream); // Zapis obrazu.
            }
        }

        // Rysuje histogram obrazu na podanym elemencie Canvas.
        private void DrawHistogram(Canvas canvas, int[][] histogram)
        {
            canvas.Children.Clear(); // Czyszczenie elementów Canvas.

            double canvasWidth = canvas.ActualWidth; // Szerokość Canvas.
            double canvasHeight = canvas.ActualHeight; // Wysokość Canvas.

            if (canvasWidth == 0 || canvasHeight == 0)
                return; // Wyjście w przypadku braku wymiarów Canvas.

            // Obliczenie maksymalnej wartości w histogramie (dla normalizacji wysokości słupków).
            int maxCount = Math.Max(histogram[0].Max(), Math.Max(histogram[1].Max(), histogram[2].Max()));

            // Obliczenie szerokości pojedynczego słupka dla jednego kanału.
            double barWidth = canvasWidth / (256 * 3); // Uwzględnienie 3 kanałów RGB.
            double scale = canvasHeight / maxCount; // Skala wysokości słupków.

            // Rysowanie słupków histogramu dla każdego kanału.
            for (int i = 0; i < 256; i++)
            {
                // Rysowanie słupka dla kanału niebieskiego.
                Rectangle blueRect = new Rectangle
                {
                    Width = barWidth,
                    Height = histogram[0][i] * scale,
                    Fill = Brushes.Blue
                };
                Canvas.SetLeft(blueRect, i * 3 * barWidth); // Pozycja X słupka.
                Canvas.SetBottom(blueRect, 0); // Pozycja Y słupka.
                canvas.Children.Add(blueRect);

                // Rysowanie słupka dla kanału zielonego.
                Rectangle greenRect = new Rectangle
                {
                    Width = barWidth,
                    Height = histogram[1][i] * scale,
                    Fill = Brushes.Green
                };
                Canvas.SetLeft(greenRect, i * 3 * barWidth + barWidth); // Pozycja X słupka.
                Canvas.SetBottom(greenRect, 0); // Pozycja Y słupka.
                canvas.Children.Add(greenRect);

                // Rysowanie słupka dla kanału czerwonego.
                Rectangle redRect = new Rectangle
                {
                    Width = barWidth,
                    Height = histogram[2][i] * scale,
                    Fill = Brushes.Red
                };
                Canvas.SetLeft(redRect, i * 3 * barWidth + 2 * barWidth); // Pozycja X słupka.
                Canvas.SetBottom(redRect, 0); // Pozycja Y słupka.
                canvas.Children.Add(redRect);
            }
        }

    }
}
