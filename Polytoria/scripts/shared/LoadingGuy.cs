// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;

namespace Polytoria.Shared;

public partial class LoadingGuy : Control
{
	private MeshInstance3D _mesh = null!;
	private double _rotationY;

	public override void _Ready()
	{
		// Use a lightweight primitive mesh instead of a full PolytorianModel
		// to reduce memory and load time during the loading screen.
		_mesh = new MeshInstance3D();
		_mesh.Mesh = new CapsuleMesh();
		_mesh.Scale = new Vector3(0.5f, 0.5f, 0.5f);
		GetNode("SubViewport").AddChild(_mesh);
		_mesh.Position = new Vector3(0, 0, -10);
		_mesh.RotationDegrees = new Vector3(0, 90, 0);
	}

	public override void _Process(double delta)
	{
		_rotationY += delta * 180.0;
		_mesh.RotationDegrees = new Vector3(0, (float)_rotationY, 0);
	}

	public override void _ExitTree()
	{
		_mesh.QueueFree();
		base._ExitTree();
	}
}
