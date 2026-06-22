namespace JdMegaMind {
  partial class MainForm {
    /// <summary>
    /// Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    /// Clean up any resources being used.
    /// </summary>
    /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
    protected override void Dispose(bool disposing) {
      if (disposing && (components != null)) {
        components.Dispose();
      }
      base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    /// <summary>
    /// Required method for Designer support - do not modify
    /// the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent() {
      this.label1 = new System.Windows.Forms.Label();
      this.SuspendLayout();
      // 
      // label1
      // 
      this.label1.Dock = System.Windows.Forms.DockStyle.Fill;
      this.label1.Font = new System.Drawing.Font("Microsoft Sans Serif", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
      this.label1.Location = new System.Drawing.Point(12, 24);
      this.label1.Name = "label1";
      this.label1.Size = new System.Drawing.Size(440, 90);
      this.label1.TabIndex = 0;
      this.label1.Text = "Welcome to your new ARC Behavior Control\r\n\r\nTo get started, follow the instructio" +
    "ns in the getting started guide for behavior controls on synthiam.com.\r\n\r\nPress F5 to compile and test this plugin. You will find it in ARC -> Project -> Add Control -> Beta";
      // 
      // MainForm
      // 
      this.ClientSize = new System.Drawing.Size(479, 149);
      this.Controls.Add(this.label1);
      this.Name = "MainForm";
      this.Text = "MainForm";
      this.ResumeLayout(false);

    }

        #endregion

        private System.Windows.Forms.Label label1;
    }
}