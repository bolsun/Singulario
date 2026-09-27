using System;
using System.Collections.Generic;
using Godot;
using Singulario.Core;
using Singulario.Views;

namespace Singulario;

public enum ToolKind
{
	NucleusBlue,
	NucleusRed,
	NucleusGreen,
	SourceYellow,
	SourceBlue,
	StarBlue,
	Blackhole,
	Singularity,
	Erase,
}

// Orchestrator: owns the GameWorld, drives the tick loop, builds all UI
// programmatically (no hand-authored .tscn Control trees, to avoid XML/UID
// mistakes that can't be caught without a compiler+editor in this session),
// and turns mouse input into world edits. Deliberately ticks from
// _Process, not _PhysicsProcess -- this is a turn-based simulation, not a
// physics one.
public partial class Main : Node2D
{
	// Leaves room above the playfield for the UI toolbar.
	const float GridOriginX = 20f;
	const float GridOriginY = 150f;

	GameWorld _world;
	Node2D _gameLayer;
	GridView _gridView;
	readonly Dictionary<int, NucleusView> _nucleusViews = new();
	readonly Dictionary<int, StarView> _starViews = new();

	ToolKind _selectedTool = ToolKind.NucleusBlue;
	bool _playing = false;
	double _tickIntervalMs = 220.0;
	double _accumMs = 0.0;

	int _nucleusSlotCount = 8;
	int _starDiameter = 2;

	Label _statusLabel;

	public override void _Ready()
	{
		_world = new GameWorld(30, 30);
		SeedInitialScene();

		_gameLayer = new Node2D { Position = new Vector2(GridOriginX, GridOriginY) };
		AddChild(_gameLayer);

		_gridView = new GridView();
		_gameLayer.AddChild(_gridView);
		_gridView.SetWorld(_world);

		RebuildAllEntityViews();
		BuildUi();
		UpdateStatusLabel();
	}

	// A small starting rig -- one source, one nucleus, one singularity --
	// mirroring the HTML prototype's seed scene, enough to see
	// capture -> transfer -> delivery happen without placing anything.
	void SeedInitialScene()
	{
		_world.PlaceSource(5, 5, "yellow");
		_world.AddNucleus(5, 6, NucleusTier.Blue, 8, 1);
		_world.PlaceSingularity(5, 7);
	}

	public override void _Process(double delta)
	{
		if (!_playing) return;
		_accumMs += delta * 1000.0;
		while (_accumMs >= _tickIntervalMs)
		{
			_accumMs -= _tickIntervalMs;
			StepOnce();
		}
	}

	void StepOnce()
	{
		Simulation.Tick(_world);
		RebuildAllEntityViews();
		RefreshAllViews();
		UpdateStatusLabel();
	}

	// ---- views ----

	void RebuildAllEntityViews()
	{
		var toRemoveN = new List<int>();
		foreach (var id in _nucleusViews.Keys)
			if (!_world.Nuclei.ContainsKey(id)) toRemoveN.Add(id);
		foreach (var id in toRemoveN)
		{
			_nucleusViews[id].QueueFree();
			_nucleusViews.Remove(id);
		}
		foreach (var kv in _world.Nuclei)
		{
			if (_nucleusViews.ContainsKey(kv.Key)) continue;
			var view = new NucleusView { Model = kv.Value };
			_gameLayer.AddChild(view);
			_nucleusViews[kv.Key] = view;
		}

		var toRemoveS = new List<int>();
		foreach (var id in _starViews.Keys)
			if (!_world.Stars.ContainsKey(id)) toRemoveS.Add(id);
		foreach (var id in toRemoveS)
		{
			_starViews[id].QueueFree();
			_starViews.Remove(id);
		}
		foreach (var kv in _world.Stars)
		{
			if (_starViews.ContainsKey(kv.Key)) continue;
			var view = new StarView { Model = kv.Value };
			_gameLayer.AddChild(view);
			_starViews[kv.Key] = view;
		}

		RepositionAllViews();
	}

	void RepositionAllViews()
	{
		foreach (var kv in _nucleusViews)
		{
			var n = _world.Nuclei[kv.Key];
			kv.Value.Position = new Vector2(
				(n.Col + 0.5f) * GridView.CellSize,
				(n.Row + 0.5f) * GridView.CellSize);
		}
		foreach (var kv in _starViews)
		{
			var s = _world.Stars[kv.Key];
			kv.Value.Position = new Vector2(
				(s.Col + s.Diameter * 0.5f) * GridView.CellSize,
				(s.Row + s.Diameter * 0.5f) * GridView.CellSize);
		}
	}

	void RefreshAllViews()
	{
		_gridView.Refresh();
		foreach (var v in _nucleusViews.Values) v.Refresh();
		foreach (var v in _starViews.Values) v.Refresh();
	}

	// ---- input ----
	// NOTE: assumes Main sits at the scene root with no camera/zoom, so a
	// mouse event's viewport position can be used directly (minus the game
	// layer's own offset). If a camera is added later this will need to go
	// through it instead.
	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mb && mb.Pressed)
		{
			Vector2 local = mb.Position - _gameLayer.Position;
			int col = (int)Math.Floor(local.X / GridView.CellSize);
			int row = (int)Math.Floor(local.Y / GridView.CellSize);
			if (!_world.InBounds(row, col)) return;

			if (mb.ButtonIndex == MouseButton.Left)
				LeftAction(row, col);
			else if (mb.ButtonIndex == MouseButton.Right)
				RightAction(row, col);
		}
	}

	void LeftAction(int row, int col)
	{
		// Clicking an already-placed nucleus toggles its spin direction.
		var existingId = _world.EntityAt[row, col];
		if (existingId.HasValue)
		{
			_world.Nuclei[existingId.Value].Dir *= -1;
			RefreshAllViews();
			return;
		}

		if (_selectedTool == ToolKind.Erase) return;

		switch (_selectedTool)
		{
			case ToolKind.NucleusBlue:
				if (_world.CellIsEmpty(row, col)) _world.AddNucleus(row, col, NucleusTier.Blue, _nucleusSlotCount);
				break;
			case ToolKind.NucleusRed:
				if (_world.CellIsEmpty(row, col)) _world.AddNucleus(row, col, NucleusTier.Red, _nucleusSlotCount);
				break;
			case ToolKind.NucleusGreen:
				if (_world.CellIsEmpty(row, col)) _world.AddNucleus(row, col, NucleusTier.Green, _nucleusSlotCount);
				break;
			case ToolKind.SourceYellow:
				if (_world.CellIsEmpty(row, col)) _world.PlaceSource(row, col, "yellow");
				break;
			case ToolKind.SourceBlue:
				if (_world.CellIsEmpty(row, col)) _world.PlaceSource(row, col, "blue");
				break;
			case ToolKind.StarBlue:
				if (_world.FootprintIsFree(row, col, _starDiameter)) _world.AddStar(row, col, _starDiameter, "blue");
				break;
			case ToolKind.Blackhole:
				if (_world.CellIsEmpty(row, col)) _world.PlaceBlackhole(row, col);
				break;
			case ToolKind.Singularity:
				if (_world.CellIsEmpty(row, col)) _world.PlaceSingularity(row, col);
				break;
		}

		RebuildAllEntityViews();
		RefreshAllViews();
	}

	void RightAction(int row, int col)
	{
		// Capture ids BEFORE erasing -- EraseCell clears the world-side
		// registries, so the id has to be read out first to free the view.
		var nid = _world.EntityAt[row, col];
		var sid = _world.StarAt[row, col];

		_world.EraseCell(row, col);

		if (nid.HasValue && _nucleusViews.TryGetValue(nid.Value, out var nv))
		{
			nv.QueueFree();
			_nucleusViews.Remove(nid.Value);
		}
		if (sid.HasValue && _starViews.TryGetValue(sid.Value, out var sv))
		{
			sv.QueueFree();
			_starViews.Remove(sid.Value);
		}

		RefreshAllViews();
	}

	// ---- UI (built entirely in code) ----

	void BuildUi()
	{
		var canvas = new CanvasLayer();
		AddChild(canvas);

		var root = new VBoxContainer
		{
			Position = new Vector2(20, 10),
			CustomMinimumSize = new Vector2(1000, 0),
		};
		canvas.AddChild(root);

		var toolRow = new HBoxContainer();
		root.AddChild(toolRow);

		var group = new ButtonGroup();
		AddToolButton(toolRow, group, "Nucleus (blue)", ToolKind.NucleusBlue, true);
		AddToolButton(toolRow, group, "Nucleus (red)", ToolKind.NucleusRed, false);
		AddToolButton(toolRow, group, "Nucleus (green)", ToolKind.NucleusGreen, false);
		AddToolButton(toolRow, group, "Source (yellow)", ToolKind.SourceYellow, false);
		AddToolButton(toolRow, group, "Source (blue)", ToolKind.SourceBlue, false);
		AddToolButton(toolRow, group, "Star (blue)", ToolKind.StarBlue, false);
		AddToolButton(toolRow, group, "Black hole", ToolKind.Blackhole, false);
		AddToolButton(toolRow, group, "Singularity", ToolKind.Singularity, false);
		AddToolButton(toolRow, group, "Erase (or right-click)", ToolKind.Erase, false);

		var controlRow = new HBoxContainer();
		root.AddChild(controlRow);

		var playButton = new Button { Text = "Play" };
		playButton.Pressed += () =>
		{
			_playing = !_playing;
			playButton.Text = _playing ? "Pause" : "Play";
		};
		controlRow.AddChild(playButton);

		var stepButton = new Button { Text = "Step" };
		stepButton.Pressed += StepOnce;
		controlRow.AddChild(stepButton);

		controlRow.AddChild(new Label { Text = "  Tick ms:" });
		var tickSpin = new SpinBox { MinValue = 20, MaxValue = 5000, Step = 10, Value = _tickIntervalMs };
		tickSpin.ValueChanged += v => _tickIntervalMs = v;
		controlRow.AddChild(tickSpin);

		controlRow.AddChild(new Label { Text = "  Capture cooldown:" });
		var cooldownCheck = new CheckBox { ButtonPressed = _world.Config.CaptureCooldownEnabled };
		cooldownCheck.Toggled += v => _world.Config.CaptureCooldownEnabled = v;
		controlRow.AddChild(cooldownCheck);

		controlRow.AddChild(new Label { Text = "  Cooldown ticks:" });
		var cooldownSpin = new SpinBox { MinValue = 0, MaxValue = 200, Step = 1, Value = _world.Config.CaptureCooldownTicks };
		cooldownSpin.ValueChanged += v => _world.Config.CaptureCooldownTicks = (int)v;
		controlRow.AddChild(cooldownSpin);

		controlRow.AddChild(new Label { Text = "  Singularity spends pools:" });
		var spendsCheck = new CheckBox { ButtonPressed = _world.Config.SingularitySpends };
		spendsCheck.Toggled += v => _world.Config.SingularitySpends = v;
		controlRow.AddChild(spendsCheck);

		var tierRow = new HBoxContainer();
		root.AddChild(tierRow);

		tierRow.AddChild(new Label { Text = "Ticks/rotation -- Blue:" });
		AddTierSpin(tierRow, NucleusTier.Blue);
		tierRow.AddChild(new Label { Text = "  Red:" });
		AddTierSpin(tierRow, NucleusTier.Red);
		tierRow.AddChild(new Label { Text = "  Green:" });
		AddTierSpin(tierRow, NucleusTier.Green);

		tierRow.AddChild(new Label { Text = "  Slot count:" });
		var slotSpin = new SpinBox { MinValue = 1, MaxValue = 8, Step = 1, Value = _nucleusSlotCount };
		slotSpin.ValueChanged += v => _nucleusSlotCount = (int)v;
		tierRow.AddChild(slotSpin);

		tierRow.AddChild(new Label { Text = "  Star diameter:" });
		var starDiaSpin = new SpinBox { MinValue = 1, MaxValue = 5, Step = 1, Value = _starDiameter };
		starDiaSpin.ValueChanged += v => _starDiameter = (int)v;
		tierRow.AddChild(starDiaSpin);

		_statusLabel = new Label();
		root.AddChild(_statusLabel);
	}

	void AddToolButton(Container parent, ButtonGroup group, string label, ToolKind tool, bool pressed)
	{
		var button = new Button
		{
			Text = label,
			ToggleMode = true,
			ButtonGroup = group,
			ButtonPressed = pressed,
		};
		button.Toggled += v => { if (v) _selectedTool = tool; };
		parent.AddChild(button);
	}

	void AddTierSpin(Container parent, NucleusTier tier)
	{
		var spin = new SpinBox
		{
			MinValue = 1,
			MaxValue = 200,
			Step = 1,
			Value = _world.Config.TierTicks.TryGetValue(tier, out var t) ? t : 8,
		};
		spin.ValueChanged += v => _world.Config.TierTicks[tier] = (int)v;
		parent.AddChild(spin);
	}

	void UpdateStatusLabel()
	{
		if (_statusLabel == null) return;
		var s = _world.Singularity;
		_statusLabel.Text =
			$"Tick: {_world.TickCounter}   Total received: {s.TotalReceivedEver:0.#}   " +
			$"Field: {_world.Rows}x{_world.Cols}   Expansions: {s.ExpansionsSoFar}   " +
			$"Crafted items delivered: {string.Join(", ", CraftedInventorySummary())}";
	}

	IEnumerable<string> CraftedInventorySummary()
	{
		if (_world.CraftedNucleusInventory.Count == 0)
		{
			yield return "none";
			yield break;
		}
		foreach (var kv in _world.CraftedNucleusInventory)
			yield return $"{kv.Key}: {kv.Value}";
	}
}
