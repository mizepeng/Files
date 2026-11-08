// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.System;
using WinRT;

namespace Files.App.Extensions
{
	/// <summary>
	/// 为基于 <see cref="ScrollViewer"/> 的列表控件提供 Shift+鼠标滚轮横向滚动。
	/// WinUI 的 ScrollViewer 本身不支持（microsoft/microsoft-ui-xaml#877）。
	/// </summary>
	public sealed class ScrollViewerShiftWheelExtensions : DependencyObject
	{
		private static readonly ConditionalWeakTable<FrameworkElement, ShiftWheelScrollController> Controllers = new();

		public static bool GetEnableShiftWheelHorizontalScrolling(DependencyObject obj)
		{
			return (bool)obj.GetValue(EnableShiftWheelHorizontalScrollingProperty);
		}

		public static void SetEnableShiftWheelHorizontalScrolling(DependencyObject obj, bool value)
		{
			obj.SetValue(EnableShiftWheelHorizontalScrollingProperty, value);
		}

		public static readonly DependencyProperty EnableShiftWheelHorizontalScrollingProperty =
			DependencyProperty.RegisterAttached(
				"EnableShiftWheelHorizontalScrolling",
				typeof(bool),
				typeof(ScrollViewerShiftWheelExtensions),
				new PropertyMetadata(false, OnEnableShiftWheelHorizontalScrollingChanged));

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private static void OnEnableShiftWheelHorizontalScrollingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			if (d is not FrameworkElement element)
				return;

			var controller = Controllers.GetValue(element, key => new ShiftWheelScrollController(key));

			if ((bool)e.NewValue)
				controller.Enable();
			else
				controller.Disable();
		}

		private sealed class ShiftWheelScrollController
		{
			// 一格滚轮（WHEEL_DELTA）的增量
			private const double WheelDelta = 120;

			// 系统设置「一次滚动多行」中每行对应的像素数
			private const double PixelsPerLine = 16;

			private const uint SPI_GETWHEELSCROLLLINES = 0x0068;

			// 系统设置为「一次滚动一个屏幕」时 SPI_GETWHEELSCROLLLINES 返回的值
			private const uint WHEEL_PAGESCROLL = uint.MaxValue;

			private readonly FrameworkElement _element;
			private readonly PointerEventHandler _wheelHandler;

			private ScrollViewer? _scrollViewer;
			private UIElement? _hookedContent;
			private bool _isEnabled;

			// 动画滚动尚未结束时连续滚动，以上一次的目标位置为基准累加，避免中途读到的偏移导致距离丢失
			private ScrollViewer? _pendingTarget;
			private double _pendingOffset;

			public ShiftWheelScrollController(FrameworkElement element)
			{
				_element = element;
				_wheelHandler = new PointerEventHandler(Content_PointerWheelChanged);
			}

			public void Enable()
			{
				if (_isEnabled)
					return;

				_isEnabled = true;
				_element.Loaded += Element_Loaded;
				_element.Unloaded += Element_Unloaded;

				if (_element.IsLoaded)
					Attach();
			}

			public void Disable()
			{
				if (!_isEnabled)
					return;

				_isEnabled = false;
				Detach();

				_element.Loaded -= Element_Loaded;
				_element.Unloaded -= Element_Unloaded;
			}

			private void Element_Loaded(object sender, RoutedEventArgs e)
			{
				Attach();
			}

			private void Element_Unloaded(object sender, RoutedEventArgs e)
			{
				Detach();
			}

			private void Attach()
			{
				var scrollViewer = _element.FindDescendant<ScrollViewer>(x => x.Name == "ScrollViewer")
					?? _element.FindDescendant<ScrollViewer>();

				// 挂在 ScrollViewer 的内容上：滚轮事件从内容冒泡到 ScrollViewer 后才会被拿去纵向滚动，
				// 在内容这一层先处理并标记 Handled，ScrollViewer 就不会再纵向滚动
				if (scrollViewer?.Content is not UIElement content || ReferenceEquals(content, _hookedContent))
					return;

				Detach();

				_scrollViewer = scrollViewer;
				_hookedContent = content;
				_hookedContent.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, false);
			}

			private void Detach()
			{
				_hookedContent?.RemoveHandler(UIElement.PointerWheelChangedEvent, _wheelHandler);
				_hookedContent = null;
				_scrollViewer = null;
				ClearPending();
			}

			private void Content_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
			{
				if (e.KeyModifiers is not VirtualKeyModifiers.Shift || _scrollViewer is null)
					return;

				var properties = e.GetCurrentPoint(null).Properties;

				// 本来就是横向的滚轮（含触控板横向滑动）交给 ScrollViewer 原样处理
				if (properties.IsHorizontalMouseWheel || properties.MouseWheelDelta == 0)
					return;

				// 没有可横向滚动的容器时不吞事件，按原来的方式处理
				var target = FindHorizontalScrollTarget(_scrollViewer);
				if (target is null)
					return;

				if (GetStepPerNotch(target) is not { } step)
					return;

				var baseOffset = ReferenceEquals(target, _pendingTarget) ? _pendingOffset : target.HorizontalOffset;
				var offset = Math.Clamp(baseOffset - properties.MouseWheelDelta / WheelDelta * step, 0, target.ScrollableWidth);

				if (!ReferenceEquals(target, _pendingTarget))
				{
					ClearPending();
					_pendingTarget = target;
					_pendingTarget.ViewChanged += PendingTarget_ViewChanged;
				}

				_pendingOffset = offset;
				target.ChangeView(offset, null, null, false);
				e.Handled = true;
			}

			private void PendingTarget_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
			{
				if (!e.IsIntermediate)
					ClearPending();
			}

			private void ClearPending()
			{
				if (_pendingTarget is not null)
					_pendingTarget.ViewChanged -= PendingTarget_ViewChanged;

				_pendingTarget = null;
			}

			/// <summary>
			/// 从自身开始向上查找第一个真正可以横向滚动的 ScrollViewer。
			/// 列布局中单列自身不能横向滚动，会找到外层承载多列的 BladeView 的 ScrollViewer。
			/// </summary>
			private static ScrollViewer? FindHorizontalScrollTarget(ScrollViewer start)
			{
				for (var current = start; current is not null; current = current.FindAscendant<ScrollViewer>())
				{
					if (current.HorizontalScrollMode is not ScrollMode.Disabled && current.ScrollableWidth > 0)
						return current;
				}

				return null;
			}

			/// <summary>
			/// 按系统「鼠标滚轮一次滚动的行数」计算每格滚动的像素数；
			/// 设为「一次滚动一个屏幕」时按可视宽度滚动。
			/// 读取系统设置失败时返回 null，记录警告并交还原有处理，不猜测步长。
			/// </summary>
			private static double? GetStepPerNotch(ScrollViewer target)
			{
				if (!SystemParametersInfo(SPI_GETWHEELSCROLLLINES, 0, out var lines, 0))
				{
					App.Logger.LogWarning("SystemParametersInfo(SPI_GETWHEELSCROLLLINES) failed with error {Error}", Marshal.GetLastPInvokeError());
					return null;
				}

				if (lines == WHEEL_PAGESCROLL)
					return target.ViewportWidth;

				return lines * PixelsPerLine;
			}

			[DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", ExactSpelling = true, SetLastError = true)]
			[return: MarshalAs(UnmanagedType.Bool)]
			private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out uint pvParam, uint fWinIni);
		}
	}
}
