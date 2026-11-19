// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Dausuva Labs
namespace ClassroomManager.Host;

public static class StudentBackupPresentation
{
    public static string Summary(int copied, int upToDate) => ModuleLanguages.StudentWork.Format($"{copied} file(s) copied, {upToDate} already there");
}
