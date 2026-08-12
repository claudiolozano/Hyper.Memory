namespace HyperMemory.Installer;

internal sealed class InstallerForm : Form
{
    private readonly TextBox _path = new() { Left = 24, Top = 112, Width = 430 };
    private readonly Button _browse = new() { Left = 462, Top = 110, Width = 76, Text = "Elegir…" };
    private readonly Button _install = new() { Left = 358, Top = 205, Width = 180, Height = 38, Text = "Instalar HyperMemory" };
    private readonly Label _status = new() { Left = 24, Top = 164, Width = 510, Height = 34 };

    public InstallerForm()
    {
        Text = "Instalar HyperMemory para Hermes";
        ClientSize = new Size(565, 270);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(new Label { Left = 24, Top = 20, Width = 515, Height = 48,
            Text = "HyperMemory añade memoria histórica permanente a Hermes.\r\nLa instalación no modifica el código ni la configuración del agente." });
        Controls.Add(new Label { Left = 24, Top = 87, Width = 500, Text = "Ubicación donde se creará la carpeta Hyper_Memory:" });
        _path.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HyperMemoryData");
        _browse.Click += Browse;
        _install.Click += InstallClicked;
        Controls.AddRange([_path, _browse, _status, _install]);
    }

    private void Browse(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog { Description = "Elige una unidad o carpeta para Hyper_Memory", UseDescriptionForTitle = true };
        if (Directory.Exists(_path.Text)) dialog.InitialDirectory = _path.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) _path.Text = dialog.SelectedPath;
    }

    private async void InstallClicked(object? sender, EventArgs e)
    {
        _install.Enabled = _browse.Enabled = _path.Enabled = false;
        _status.Text = "Instalando y conectando con Hermes…";
        try
        {
            var manifest = await Task.Run(() => Program.Install(_path.Text));
            _status.Text = "Instalación completada.";
            MessageBox.Show(this,
                $"HyperMemory está instalado y funcionando.\n\nMemoria: {manifest.StorageRoot}\n\nCierra y vuelve a abrir Hermes para activar el Skill. Puedes desinstalarlo desde Configuración > Aplicaciones.",
                "HyperMemory", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception error)
        {
            _status.Text = "No se pudo completar la instalación.";
            MessageBox.Show(this, error.Message, "HyperMemory", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _install.Enabled = _browse.Enabled = _path.Enabled = true;
        }
    }
}
