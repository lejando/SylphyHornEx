using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using JetBrains.Annotations;
using SylphyHorn.Serialization;
using static SylphyHorn.Interop.NativeMethods;

namespace SylphyHorn.Services
{
	public class AlwaysOnTopService : IDisposable
	{
		public static AlwaysOnTopService Instance { get; } = new AlwaysOnTopService();

		private readonly Dictionary<IntPtr, IntPtr> _windowHandles = new Dictionary<IntPtr, IntPtr>();
		private const string _menuItemName = "Always on top";
		private const int _menuItemId = 45545;
		private readonly Thread _hookThread;
		private uint _hookThreadId;
		private IntPtr _globalHook;
		private volatile bool _stopping;

		private struct MenuItemResult
		{
			[CanBeNull]
			public MENUITEMINFO MenuItemInfo;

			public uint Index;

			public bool IsExisting()
			{
				return this.MenuItemInfo != null;
			}
		}

		private AlwaysOnTopService()
		{
			this._hookThread = new Thread(() => this.HookThreadMain());
			this._hookThread.Start();
		}

		private void HookThreadMain()
		{
			this._hookThreadId = (uint)AppDomain.GetCurrentThreadId();
			// requires message queue running on same thread
			this._globalHook = SetWinEventHook(
				EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS, IntPtr.Zero,
				this.WinEventObjectFocus, 0, 0, WINEVENT_OUTOFCONTEXT);
			if (this._globalHook == IntPtr.Zero)
			{
				LoggingService.Instance.Register(new Exception("Couldn't hook windows event"));
				return;
			}

			while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
			{
				if (message.Message == WM_QUIT)
					break;
				TranslateMessage(ref message);
				DispatchMessage(ref message);
			}
		}

		private void WinEventObjectFocus(
			IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
			int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
		{
			try
			{
				if (!this._stopping)
					this.ProcessSysMenu(GetForegroundWindow());
			}
			catch (Exception e)
			{
				Debug.WriteLine($"{e.Message}\n{e.StackTrace}");
			}
		}

		private MenuItemResult FindMenuItem(IntPtr menuHwnd)
		{
			if (menuHwnd == IntPtr.Zero)
				return new MenuItemResult() { Index = 0, MenuItemInfo = null };

			var items = GetMenuItemCount(menuHwnd);

			for (uint i = 0; i < items; i++)
			{
				var item = new MENUITEMINFO(MIIM.STATE | MIIM.FTYPE | MIIM.ID | MIIM.STRING);
				item.dwTypeData = new string(' ', 64);
				item.cch = (uint)item.dwTypeData.Length;
				if (!GetMenuItemInfo(menuHwnd, i, true, item))
					continue;
				if (item.dwTypeData == _menuItemName && item.wID == _menuItemId)
				{
					return new MenuItemResult() { Index = i, MenuItemInfo = item };
				}
			}

			return new MenuItemResult() { Index = Math.Max(0, (uint)items - 2), MenuItemInfo = null };
		}

		private void ProcessSysMenu(IntPtr windowHwnd)
		{
			if (windowHwnd == IntPtr.Zero)
				return;
			if (!Settings.General.ShowAlwaysOnTopItem)
				return;
			var menuHwnd = GetSystemMenu(windowHwnd, false);
			if (menuHwnd == IntPtr.Zero)
				return;
			var result = this.FindMenuItem(menuHwnd);
			var actualState = this.IsTopmost(windowHwnd) ? MFS_CHECKED : MFS_UNCHECKED;
			if (result.IsExisting() && (result.MenuItemInfo.fState & MFS_CHECKED) == (actualState & MFS_CHECKED))
			{
				Debug.WriteLine($"{windowHwnd} State is good, returning");
				return;
			}

			if (result.IsExisting())
			{
				Debug.WriteLine($"{windowHwnd} Setting menu item");
				result.MenuItemInfo.fState &= ~MFS_CHECKED;
				result.MenuItemInfo.fState |= actualState;
				SetMenuItemInfo(menuHwnd, result.Index, true, result.MenuItemInfo);
			}
			else
			{
				Debug.WriteLine($"{windowHwnd} Inserting menu item");
				var newItem = this.BuildMenuItem(actualState);
				InsertMenuItem(menuHwnd, result.Index, true, newItem);
			}

			if (!this._windowHandles.ContainsValue(windowHwnd) && !this._stopping)
			{
				Debug.WriteLine($"{windowHwnd} Setting click hook");
				var hook = SetWinEventHook(
					EVENT_OBJECT_INVOKED, EVENT_OBJECT_INVOKED, windowHwnd,
					this.WinEventObjectInvoked, 0, 0, WINEVENT_OUTOFCONTEXT);
				if (hook != IntPtr.Zero)
				{
					Debug.WriteLine($"{windowHwnd} Set click hook");
					this._windowHandles[hook] = windowHwnd;
				}
			}
		}

		private MENUITEMINFO BuildMenuItem(uint state)
		{
			var newItem = new MENUITEMINFO(MIIM.STATE | MIIM.FTYPE | MIIM.ID | MIIM.STRING);
			newItem.fType = MFT_STRING;
			newItem.dwTypeData = _menuItemName;
			newItem.cch = (uint)newItem.dwTypeData.Length;
			newItem.fState = state;
			newItem.wID = _menuItemId;
			return newItem;
		}

		private bool IsTopmost(IntPtr hwnd)
		{
			var info = new WINDOWINFO(true);
			GetWindowInfo(hwnd, ref info);
			return (info.dwExStyle & WS_EX_TOPMOST) != 0;
		}

		private void WinEventObjectInvoked(
			IntPtr hWinEventHook, uint eventType,
			IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
		{
			try
			{
				if (!this._stopping)
					this.OnItemClicked(idChild, hWinEventHook);
			}
			catch (Exception e)
			{
				Debug.WriteLine($"{e.Message}\n{e.StackTrace}");
			}
		}

		private void OnItemClicked(int idChild, IntPtr hWinEventHook)
		{
			if (!Settings.General.ShowAlwaysOnTopItem)
				return;
			if (idChild != _menuItemId)
				return;
			if (!this._windowHandles.TryGetValue(hWinEventHook, out var windowHwnd))
				return;
			if (GetForegroundWindow() != windowHwnd)
				return;
			SetWindowPos(
				windowHwnd, this.IsTopmost(windowHwnd) ? HWND_NOTOPMOST : HWND_TOPMOST,
				0, 0, 0, 0, SetWindowPosFlags.IgnoreMove | SetWindowPosFlags.IgnoreResize);
			this.ProcessSysMenu(windowHwnd);
		}

		public void Dispose()
		{
			if (this._hookThread == null) return;

			Debug.WriteLine("Disposing");
			this._stopping = true;
			foreach (var pair in this._windowHandles)
			{
				var windowHwnd = pair.Value;
				var hookHwnd = pair.Key;

				Debug.WriteLine($"{windowHwnd} Hook {hookHwnd}");
				var menuHwnd = GetSystemMenu(windowHwnd, true);

				var result = this.FindMenuItem(menuHwnd);
				if (result.IsExisting())
				{
					Debug.WriteLine($"{windowHwnd} Is existing");
					RemoveMenu(windowHwnd, result.Index, true);
				}

				UnhookWinEvent(hookHwnd);
			}

			Debug.Flush();
			this._windowHandles.Clear();
			if (!PostThreadMessage(this._hookThreadId, WM_QUIT, UIntPtr.Zero, IntPtr.Zero))
				Debug.WriteLine(Marshal.GetLastWin32Error());
			this._hookThread.Join();
		}
	}
}
