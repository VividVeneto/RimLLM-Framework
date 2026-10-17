using System;
using UnityEngine;
using Verse;

namespace RimLLM_Framework.Mod;

/// <summary>
/// 手動填寫模型上下文上限（token 數）的小視窗。填 0 代表清除手動值、改用 API 回報的值。
/// </summary>
#pragma warning disable S101 // Follows RimWorld core engine naming convention (Dialog_*) for Window subclasses
public class Dialog_SetContextWindow : Window
#pragma warning restore S101
{
    private const int MaxTokens = 100000000;

    private readonly string _entry;
    private readonly Action<int> _onConfirmed;
    private int _tokens;
    private string _buffer;

    public override Vector2 InitialSize => new(420f, 200f);

    public Dialog_SetContextWindow(string entry, int currentTokens, Action<int> onConfirmed)
    {
        _entry = entry;
        _tokens = currentTokens;
        _onConfirmed = onConfirmed;
        doCloseX = true;
        // 點到視窗外就關閉會讓已輸入的值無聲遺失，只保留右上角 X 與確定兩種離開方式
        closeOnClickedOutside = false;
        absorbInputAroundWindow = true;
    }

    public override void DoWindowContents(Rect inRect)
    {
        Widgets.Label(new Rect(0f, 0f, inRect.width, 30f), "RimLLM_ContextWindowDialogTitle".Translate(_entry));
        Widgets.Label(new Rect(0f, 32f, inRect.width, 50f), "RimLLM_ContextWindowDialogHint".Translate());

        Widgets.TextFieldNumeric(new Rect(0f, 88f, inRect.width, 30f), ref _tokens, ref _buffer, 0f, MaxTokens);

        if (Widgets.ButtonText(new Rect(inRect.width - 120f, inRect.height - 32f, 120f, 32f), "OK".Translate()))
        {
            Confirm();
        }
    }

    /// <summary>
    /// Enter 視同按下確定。基底 <see cref="Window.OnAcceptKeyPressed"/> 在 closeOnAccept 為 true 時
    /// 只會關閉視窗而不儲存，玩家輸入數字後按 Enter 的值就會遺失。
    /// </summary>
    public override void OnAcceptKeyPressed()
    {
        Event.current.Use();
        Confirm();
    }

    private void Confirm()
    {
        _onConfirmed?.Invoke(_tokens);
        Close();
    }
}
