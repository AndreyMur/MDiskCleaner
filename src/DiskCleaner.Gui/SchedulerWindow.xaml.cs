using System.IO;
using System.Windows;
using DiskCleaner.Core.Scheduling;

namespace DiskCleaner.Gui;

public partial class SchedulerWindow : Window
{
    private static readonly string[] RussianDays =
        ["Понедельник", "Вторник", "Среда", "Четверг", "Пятница", "Суббота", "Воскресенье"];

    private readonly SchedulerService _scheduler;
    private readonly string _applicationPath;

    public SchedulerWindow(SchedulerService scheduler, string applicationPath)
    {
        InitializeComponent();
        _scheduler = scheduler;
        _applicationPath = applicationPath;

        DayComboBox.ItemsSource = RussianDays;
        for (var hour = 0; hour < 24; hour++)
        {
            HourComboBox.Items.Add(hour.ToString("00"));
        }

        for (var minute = 0; minute < 60; minute += 5)
        {
            MinuteComboBox.Items.Add(minute.ToString("00"));
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        DayComboBox.SelectedIndex = 6;
        HourComboBox.SelectedIndex = 10;
        MinuteComboBox.SelectedIndex = 0;
        EnabledCheckBox.IsChecked = false;

        try
        {
            var state = await _scheduler.QueryAsync();
            if (state.Exists && state.Schedule is not null)
            {
                ApplySchedule(state.Schedule);
            }
        }
        catch (Exception ex)
        {
            ErrorText.Text = "Не удалось прочитать текущее расписание: " + ex.Message;
        }
    }

    private void ApplySchedule(CleanSchedule schedule)
    {
        EnabledCheckBox.IsChecked = schedule.Enabled;
        DayComboBox.SelectedIndex = IndexOfDay(schedule.Day);
        HourComboBox.SelectedIndex = Math.Clamp(schedule.Time.Hours, 0, 23);
        MinuteComboBox.SelectedIndex = Math.Max(0, MinuteComboBox.Items.IndexOf(schedule.Time.Minutes.ToString("00")));
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var schedule = new CleanSchedule
        {
            Enabled = EnabledCheckBox.IsChecked == true,
            Day = DayOfWeekFromIndex(DayComboBox.SelectedIndex),
            Time = new TimeSpan(
                HourComboBox.SelectedIndex,
                MinuteComboBox.SelectedIndex * 5,
                0)
        };

        SaveButton.IsEnabled = false;
        try
        {
            var error = await _scheduler.SaveAsync(schedule, _applicationPath);
            if (error is not null)
            {
                ErrorText.Text = error;
                return;
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        DeleteButton.IsEnabled = false;
        try
        {
            var error = await _scheduler.DeleteAsync();
            if (error is not null)
            {
                ErrorText.Text = error;
                return;
            }

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
        finally
        {
            DeleteButton.IsEnabled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static int IndexOfDay(DayOfWeek day) => day == DayOfWeek.Sunday ? 6 : (int)day - 1;

    private static DayOfWeek DayOfWeekFromIndex(int index)
    {
        if (index < 0)
        {
            return DayOfWeek.Sunday;
        }

        return index == 6 ? DayOfWeek.Sunday : (DayOfWeek)(index + 1);
    }
}
