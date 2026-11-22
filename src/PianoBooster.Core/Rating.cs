// Ported from Rating.h and Rating.cpp of PianoBooster (C++). Copyright (c) 2008-2013, L. J. Barman. GPLv3.

namespace PianoBooster.Core;

/// <summary>Counts the good, wrong and late notes, and the accuracy shown on the score.</summary>
public sealed class Rating
{
    private int _totalNotesCount;
    private int _previousNoteCount;
    private int _lateNoteCount;
    private int _previousLateNoteCount;
    private int _wrongNoteCount;
    private float _currentAccuracy;
    private float _factor;
    private PbColor _currentColor;
    private bool _goodAccuracyFlag;

    private readonly struct AccuracyItem
    {
        public readonly float Value;
        public readonly float StepUp;
        public readonly float StepDown;
        public readonly PbColor Color;

        public AccuracyItem(float value, float stepUp, float stepDown, PbColor color)
        {
            Value = value;
            StepUp = stepUp;
            StepDown = stepDown;
            Color = color;
        }
    }

    private static readonly AccuracyItem[] s_accuracyTable =
    {
        new(1.00f, 0.01f, -0.08f, new PbColor(0.5, 0.5, 1.0)), // Only the color is used on the top score
        new(0.75f, 0.01f, -0.07f, new PbColor(0.7, 0.3, 1.0)),
        new(0.50f, 0.01f, -0.05f, new PbColor(1.0, 0.6, 0.4)),
        new(0.25f, 0.01f, -0.03f, new PbColor(1.0, 0.3, 1.0)),
        new(0.00f, 0.015f, -0.02f, new PbColor(1.0, 0.4, 0.2)),
    };

    public Rating()
    {
        Reset();
    }

    public void Reset()
    {
        _totalNotesCount = 0;
        _wrongNoteCount = 0;
        _lateNoteCount = 0;
        _currentAccuracy = 0.5f;
        _previousNoteCount = -1;
        _previousLateNoteCount = 0;
        _factor = 2.0f;
        _goodAccuracyFlag = false;
    }

    public void TotalNotes(int count) => _totalNotesCount += count;
    public void WrongNotes(int count) => _wrongNoteCount += count;
    public void LateNotes(int count) => _lateNoteCount += count;
    public int TotalNoteCount => _totalNotesCount;
    public int WrongNoteCount => _wrongNoteCount;
    public int LateNoteCount => _lateNoteCount;

    /// <summary>The percentage of the notes that were not late.</summary>
    public double Percent
    {
        get
        {
            double percent = 100;
            if (_totalNotesCount > 0)
                percent = ((_totalNotesCount - _lateNoteCount) * 100.0) / _totalNotesCount;
            return percent;
        }
    }

    public void CalculateAccuracy()
    {
        int direction = 0;

        if (_previousNoteCount != _totalNotesCount)
        {
            _previousNoteCount = _totalNotesCount;
            direction = 1;
        }

        if (_previousLateNoteCount != _lateNoteCount)
        {
            _previousLateNoteCount = _lateNoteCount;
            direction = -1;
        }

        if (direction != 0)
        {
            for (int i = 0; i < s_accuracyTable.Length; i++)
            {
                if (_currentAccuracy >= s_accuracyTable[i].Value || i + 1 == s_accuracyTable.Length)
                {
                    float stepAmount = (direction > 0) ? s_accuracyTable[i].StepUp : s_accuracyTable[i].StepDown;
                    if (stepAmount < 0 && Conductor.PlayMode == PlayMode.PlayAlong)
                        stepAmount = s_accuracyTable[i].StepUp + s_accuracyTable[i].StepDown;
                    _currentAccuracy += stepAmount * _factor;
                    break;
                }
            }

            if (_currentAccuracy < 0) _currentAccuracy = 0;
            if (_currentAccuracy > 1) _currentAccuracy = 1;

            _goodAccuracyFlag = false;

            for (int i = 0; i < s_accuracyTable.Length; i++)
            {
                if (_currentAccuracy >= s_accuracyTable[i].Value || i + 1 == s_accuracyTable.Length)
                {
                    _currentColor = s_accuracyTable[i].Color;
                    if (_currentAccuracy >= s_accuracyTable[1].Value) // the second row in the table
                        _goodAccuracyFlag = true;
                    break;
                }
            }
        }
    }

    public float AccuracyValue => _currentAccuracy;
    public PbColor AccuracyColor => _currentColor;
    public bool IsAccuracyGood => _goodAccuracyFlag;
}
