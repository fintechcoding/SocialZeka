using System.Threading;
using System.Windows;

namespace VoiceTranscript.Tests;

/// <summary>
/// The control the backup password is typed into.
///
/// This is the one piece of the locked-backup path that no other test touches, and the one whose
/// failure is silent in the worst way: a password box that reports nothing produces a backup that
/// is not encrypted at all, the restore never asks for a password, and the user concludes the
/// password was ignored. The service layer is verified elsewhere and is correct — so if the
/// answer ever goes missing, it goes missing here.
/// </summary>
public class PasswordBoxTests
{
    [Fact]
    public void WhatIsTypedIsWhatComesBack()
    {
        string? readBack = null;
        Exception? failure = null;

        // STA, because a WPF control cannot be constructed anywhere else. Its own thread rather
        // than a shared one: the smoke test owns the Application and its resources.
        var thread = new Thread(() =>
        {
            try
            {
                var box = new Wpf.Ui.Controls.PasswordBox();

                // ApplyTemplate, because this control keeps its value in a template part. A test
                // that only sets and reads the property would pass on a box that shows nothing
                // and returns nothing to the dialog.
                box.Measure(new Size(300, 40));
                box.ApplyTemplate();

                box.Password = "parolam 123";
                readBack = box.Password;
            }
            catch (Exception e)
            {
                failure = e;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.Equal("parolam 123", readBack);
    }

    [Fact]
    public void RevealingAndHidingKeepsIt()
    {
        // The dialog copies between a password box and a plain one when "Parolayı göster" is
        // toggled. A round trip that loses the value there is a backup encrypted under "".
        string? afterRoundTrip = null;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                var box = new Wpf.Ui.Controls.PasswordBox();
                var shown = new Wpf.Ui.Controls.TextBox();

                box.Measure(new Size(300, 40));
                box.ApplyTemplate();

                box.Password = "gizli";

                shown.Text = box.Password;   // reveal
                box.Password = shown.Text;   // hide again

                afterRoundTrip = box.Password;
            }
            catch (Exception e)
            {
                failure = e;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.Equal("gizli", afterRoundTrip);
    }
}
