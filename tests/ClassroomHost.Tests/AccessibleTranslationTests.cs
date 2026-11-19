// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
using System.Runtime.ExceptionServices;
using System.Windows.Automation;
using System.Windows.Controls;
using ClassroomManager.Host;
using ClassroomManager.Shared;
using Xunit;

namespace ClassroomHost.Tests;

[Collection(nameof(LanguageSwitching))]
public sealed class AccessibleTranslationTests
{
    [Fact]
    public void Input_metadata_translates_without_touching_typed_text_passwords_or_ids()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            var previous = Loc.Current;
            try
            {
                Loc.Use(Loc.BuiltIn(Loc.Lithuanian));
                var panel = new StackPanel();
                var text = new TextBox { Text = "Search" }; // A value that would translate if accidentally visited.
                var password = new PasswordBox { Password = "Close" };
                panel.Children.Add(text);
                panel.Children.Add(password);
                foreach (var field in new Control[] { text, password })
                {
                    AutomationProperties.SetName(field, "Search");
                    AutomationProperties.SetHelpText(field, "Close");
                    AutomationProperties.SetAutomationId(field, "StableId");
                }
                Assert.True(UiTranslator.TranslateTree(panel));
                foreach (var field in new Control[] { text, password })
                {
                    Assert.Equal(Loc.T("Search"), AutomationProperties.GetName(field));
                    Assert.Equal(Loc.T("Close"), AutomationProperties.GetHelpText(field));
                    Assert.Equal("StableId", AutomationProperties.GetAutomationId(field));
                }
                Assert.Equal("Search", text.Text);
                Assert.Equal("Close", password.Password);
                Assert.False(UiTranslator.TranslateTree(panel)); // Repeated layout passes settle.
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally { Loc.Use(previous); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
