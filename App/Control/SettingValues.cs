using System;

namespace OmenSuperHub.Control {
  internal static class SettingValues {
    internal static int? Number(string text, string unit, int minimum, int maximum) {
      int value;
      if (text == "max") return maximum;
      string number = string.IsNullOrEmpty(unit) ? text ?? "" : (text ?? "").Replace(unit, "");
      return int.TryParse(number.Trim(), out value) && value >= minimum && value <= maximum ? value : (int?)null;
    }
  }
}
