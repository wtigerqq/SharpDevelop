﻿// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Refactoring;

namespace ICSharpCode.AvalonEdit.AddIn.ContextActions
{
	/// <summary>
	/// Renders Popup with context actions on the left side of the current line in the editor.
	/// </summary>
	public sealed class ContextActionsRenderer : IDisposable
	{
		readonly CodeEditorView editorView;
		ObservableCollection<IContextActionProvider> providers = new ObservableCollection<IContextActionProvider>();
		
		ITextEditor Editor { get { return this.editorView.Adapter; } }
		
		/// <summary>
		/// This popup is reused (closed and opened again).
		/// </summary>
		ContextActionsBulbPopup popup;
		
		/// <summary>
		/// Delays the available actions resolution so that it does not get called too often when user holds an arrow.
		/// </summary>
		DispatcherTimer delayMoveTimer;
		const int delayMoveMilliseconds = 500;
		
		public ContextActionsRenderer(CodeEditorView editor)
		{
			if (editor == null)
				throw new ArgumentNullException("editor");
			this.editorView = editor;
			
			this.editorView.TextArea.Caret.PositionChanged += CaretPositionChanged;
			
			this.editorView.KeyDown += new KeyEventHandler(ContextActionsRenderer_KeyDown);
			providers.CollectionChanged += providers_CollectionChanged;
			
			editor.TextArea.TextView.ScrollOffsetChanged += ScrollChanged;
			this.delayMoveTimer = new DispatcherTimer() { Interval = TimeSpan.FromMilliseconds(delayMoveMilliseconds) };
			this.delayMoveTimer.Stop();
			this.delayMoveTimer.Tick += TimerMoveTick;
			SD.Workbench.ActiveViewContentChanged += WorkbenchSingleton_Workbench_ActiveViewContentChanged;
		}
		
		public void Dispose()
		{
			SD.Workbench.ActiveViewContentChanged -= WorkbenchSingleton_Workbench_ActiveViewContentChanged;
			ClosePopup();
		}
		
		public IList<IContextActionProvider> Providers {
			get { return providers; }
		}
		
		void providers_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
		{
			StartTimer();
		}
		
		async void ContextActionsRenderer_KeyDown(object sender, KeyEventArgs e)
		{
			if (e.Key == Key.T && Keyboard.Modifiers == ModifierKeys.Control) {
				if (popup == null)
					popup = new ContextActionsBulbPopup(editorView.TextArea);
				if (popup.IsOpen && popup.ViewModel != null && popup.ViewModel.Actions != null && popup.ViewModel.Actions.Count > 0) {
					popup.IsDropdownOpen = true;
					popup.Focus();
				} else {
					ClosePopup();
					// Popup is not shown but user explicitely requests it
					var popupVM = BuildPopupViewModel();
					this.cancellationTokenSourceForPopupBeingOpened = new CancellationTokenSource();
					var cancellationToken = cancellationTokenSourceForPopupBeingOpened.Token;
					try {
						await popupVM.LoadActionsAsync(cancellationToken);
						if (popupVM.Actions.Count == 0)
							await popupVM.LoadHiddenActionsAsync(cancellationToken);
					} catch (OperationCanceledException) {
						return;
					}
					if (cancellationToken.IsCancellationRequested)
						return;
					this.cancellationTokenSourceForPopupBeingOpened = null;
					if (popupVM.Actions.Count == 0 && popupVM.HiddenActions.Count == 0)
						return;
					this.popup.ViewModel = popupVM;
					this.popup.IsDropdownOpen = true;
					this.popup.IsHiddenActionsExpanded = popupVM.Actions.Count == 0;
					this.popup.OpenAtLineStart(this.Editor);
					this.popup.Focus();
				}
			}
		}

		void ScrollChanged(object sender, EventArgs e)
		{
			StartTimer();
		}
		
		CancellationTokenSource cancellationTokenSourceForPopupBeingOpened;
		
		async void TimerMoveTick(object sender, EventArgs e)
		{
			if (!delayMoveTimer.IsEnabled)
				return;
			ClosePopup();
			
			// Don't show the context action popup when the caret is outside the editor boundaries
			var textView = this.editorView.TextArea.TextView;
			Rect editorRect = new Rect((Point)textView.ScrollOffset, textView.RenderSize);
			Rect caretRect = this.editorView.TextArea.Caret.CalculateCaretRectangle();
			if (!editorRect.Contains(caretRect))
				return;
			
			ContextActionsBulbViewModel popupVM = BuildPopupViewModel();
			this.cancellationTokenSourceForPopupBeingOpened = new CancellationTokenSource();
			var cancellationToken = cancellationTokenSourceForPopupBeingOpened.Token;
			try {
				await popupVM.LoadActionsAsync(cancellationToken);
			} catch (OperationCanceledException) {
				LoggingService.Debug("Cancelled loading context actions.");
				return;
			}
			if (cancellationToken.IsCancellationRequested)
				return;
			this.cancellationTokenSourceForPopupBeingOpened = null;
			if (popupVM.Actions.Count == 0)
				return;
			if (popup == null)
				popup = new ContextActionsBulbPopup(editorView.TextArea);
			this.popup.ViewModel = popupVM;
			this.popup.OpenAtLineStart(this.Editor);
		}
		
		ContextActionsBulbViewModel BuildPopupViewModel()
		{
			var actionsProvider = new EditorActionsProvider(new EditorRefactoringContext(this.Editor), providers.ToArray());
			return new ContextActionsBulbViewModel(actionsProvider);
		}

		void CaretPositionChanged(object sender, EventArgs e)
		{
			StartTimer();
		}
		
		void StartTimer()
		{
			ClosePopup();
			if (providers.Count == 0)
				return;
			IViewContent activeViewContent = SD.Workbench.ActiveViewContent;
			if (activeViewContent != null && activeViewContent.PrimaryFileName == this.Editor.FileName)
				delayMoveTimer.Start();
		}
		
		void ClosePopup()
		{
			if (cancellationTokenSourceForPopupBeingOpened != null) {
				LoggingService.Debug("Loading context actions - requesting cancellation.");
				cancellationTokenSourceForPopupBeingOpened.Cancel();
				cancellationTokenSourceForPopupBeingOpened = null;
			}
			
			this.delayMoveTimer.Stop();
			if (popup != null) {
				this.popup.Close();
				this.popup.IsDropdownOpen = false;
				this.popup.IsHiddenActionsExpanded = false;
				this.popup.ViewModel = null;
			}
		}
		void WorkbenchSingleton_Workbench_ActiveViewContentChanged(object sender, EventArgs e)
		{
			// open the popup again if in current file
			StartTimer();
		}
	}
}
