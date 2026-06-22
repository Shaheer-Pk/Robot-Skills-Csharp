using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace JdMegaMind {

  public partial class ConfigForm : Form {

    private Configuration _cf;

    public ConfigForm() {

      InitializeComponent();
    }

    public void SetConfiguration(Configuration config) {

      try {

        _cf = config;

        tbTestValue.Text = config.TestValue;

      } catch (Exception ex) {

        MessageBox.Show(ex.Message, "Error loading configuration");
      }
    }

    public Configuration GetConfiguration() {
    
      return _cf;
    }

    private void btnSave_Click(object sender, EventArgs e) {

      try {

        _cf.TestValue = tbTestValue.Text;
      } catch (Exception ex) {

        MessageBox.Show(ex.Message, "Error saving configuration");

        return;
      }

      DialogResult = DialogResult.OK;
    }

    private void btnCancel_Click(object sender, EventArgs e) {

      DialogResult = DialogResult.Cancel;
    }
  }
}
