// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.ViewModels.Previews;
using Files.Shared.Helpers;

namespace Files.App.Helpers
{
	public static class AdaptiveLayoutHelpers
	{
		private static ILayoutSettingsService LayoutSettingsService { get; } = Ioc.Default.GetRequiredService<ILayoutSettingsService>();
		private static IContentPageContext ContentPageContext { get; } = Ioc.Default.GetRequiredService<IContentPageContext>();
		private static IWindowsIniService WindowsIniService { get; } = Ioc.Default.GetRequiredService<IWindowsIniService>();
		private static IFoldersSettingsService FoldersSettingsService { get; } = Ioc.Default.GetRequiredService<IFoldersSettingsService>();

		// 项数超过该值的目录按内容判定时固定用详细信息：导航时预测无需统计全部项，枚举完成后也不再切换布局
		private const int MaxGridLayoutItems = 5000;

		// Predicts the layout from desktop.ini or a count of the directory entries (up to a limit), so navigation opens in the right layout without a post-enumeration switch
		public static bool TryPredictLayout(string? path, out FolderLayoutModes layout)
		{
			layout = FolderLayoutModes.DetailsView;

			if (path is null)
				return false;

			try
			{
				var root = SystemIO.Path.GetPathRoot(path);
				if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) ||
					new SystemIO.DriveInfo(root).DriveType is SystemIO.DriveType.Network or SystemIO.DriveType.NoRootDirectory)
					return false;

				var viewStateSection = WindowsIniService.GetData(SystemIO.Path.Combine(path, "desktop.ini"))
					.FirstOrDefault(x => x.SectionName == "ViewState");
				if (viewStateSection is not null)
				{
					var viewMode = viewStateSection.Parameters.FirstOrDefault(x => x.Key == "Mode").Value;
					layout = viewMode is "Pictures" or "Videos" ? FolderLayoutModes.GridView : FolderLayoutModes.DetailsView;
					return true;
				}

				// 按与枚举完成后判定（GetContentLayout）相同的口径统计全部项：只取文件名和属性，不读其他元数据，
				// 抽样只看前若干项会与最终判定不一致，打开后先显示一种布局、加载完又跳到另一种
				bool showHiddenItems = FoldersSettingsService.ShowHiddenItems;
				bool showProtectedSystemFiles = FoldersSettingsService.ShowProtectedSystemFiles;
				bool showDotFiles = FoldersSettingsService.ShowDotFiles;

				int total = 0, media = 0;
				foreach (var entry in new SystemIO.DirectoryInfo(path).EnumerateFileSystemInfos())
				{
					// 与 Win32StorageEnumerator 的显示过滤规则保持一致
					var isHidden = entry.Attributes.HasFlag(SystemIO.FileAttributes.Hidden);
					var isSystem = entry.Attributes.HasFlag(SystemIO.FileAttributes.System);
					if (isHidden && (!showHiddenItems || (isSystem && !showProtectedSystemFiles)))
						continue;
					if (entry.Name.StartsWith('.') && !showDotFiles)
						continue;

					// 超大目录直接定为详细信息，不必统计完
					if (++total > MaxGridLayoutItems)
						return true;
					if (entry is SystemIO.FileInfo && IsMediaExtension(entry.Extension))
						media++;
				}

				if (total is 0)
					return false;

				layout = 100f * media / total > 60f ? FolderLayoutModes.GridView : FolderLayoutModes.DetailsView;
				return true;
			}
			catch (Exception ex) when (ex is SystemIO.IOException or UnauthorizedAccessException or ArgumentException)
			{
				return false;
			}
		}

		public static void ApplyAdaptativeLayout(LayoutPreferencesManager folderSettings, IList<ListedItem> filesAndFolders)
		{
			if (LayoutSettingsService.SyncFolderPreferencesAcrossDirectories)
				return;
			if (folderSettings.IsLayoutModeFixed || !folderSettings.IsAdaptiveLayoutEnabled)
				return;

			switch (GetAdaptiveLayout(filesAndFolders))
			{
				case Layouts.Detail when folderSettings.LayoutMode is not FolderLayoutModes.DetailsView:
					folderSettings.ToggleLayoutModeDetailsView(false);
					break;
				case Layouts.Grid when folderSettings.LayoutMode is not FolderLayoutModes.GridView:
					folderSettings.ToggleLayoutModeGridView(false);
					break;
			}
		}

		private static Layouts GetAdaptiveLayout(IList<ListedItem> filesAndFolders)
		{
			var pathLayout = GetPathLayout();
			if (pathLayout is not Layouts.None)
				return pathLayout;

			return GetContentLayout(filesAndFolders);
		}

		private static Layouts GetPathLayout()
		{
			var desktopIni = ContentPageContext.ShellPage?.ShellViewModel?.DesktopIni;
			if (desktopIni is null)
				return Layouts.None;

			var viewStateSection = desktopIni.FirstOrDefault(x => x.SectionName == "ViewState");
			if (viewStateSection is null)
				return Layouts.None;

			var viewMode = viewStateSection.Parameters.FirstOrDefault(x => x.Key == "Mode").Value;

			return viewMode switch
			{
				"Pictures" => Layouts.Grid,
				"Videos" => Layouts.Grid,
				_ => Layouts.Detail,
			};
		}

		private static Layouts GetContentLayout(IList<ListedItem> filesAndFolders)
		{
			int itemCount = filesAndFolders.Count;
			if (filesAndFolders.Count is 0)
				return Layouts.None;

			// 与 TryPredictLayout 一致，超大目录固定用详细信息
			if (itemCount > MaxGridLayoutItems)
				return Layouts.Detail;

			float mediaPercentage = 100f * filesAndFolders.Count(IsMedia) / itemCount;

			if (mediaPercentage > 60f)
				return Layouts.Grid;
			return Layouts.Detail;

			static bool IsMedia(ListedItem item)
				=> IsMediaExtension(item.FileExtension);
		}

		private static bool IsMediaExtension(string? extension)
			=> !string.IsNullOrEmpty(extension)
			&& (FileExtensionHelpers.IsAudioFile(extension)
			|| FileExtensionHelpers.IsVideoFile(extension)
			|| FileExtensionHelpers.IsImageFile(extension));

		private enum Layouts
		{
			None, // Don't decide. Another function to decide can be called afterwards if available.
			Detail, // Apply the layout Detail.
			Grid, // Apply the layout Grid.
		}
	}
}
