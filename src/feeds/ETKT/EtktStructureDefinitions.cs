using System.Collections.ObjectModel;

namespace PresidioDemo.Feeds.ETKT;

/// <summary>
/// Screenshot history order, with a separately identified observed type-5 prefix.
/// </summary>
public static class EtktStructureDefinitions
{

    #region Methods

    /// <summary>
    /// Constructs the reviewed field declarations in their required wire order.
    /// </summary>
    /// <returns>The complete reviewed declarations in their required wire order.</returns>
    private static IReadOnlyList<EtktFieldDefinition> Build()
    {
        var fields = new List<EtktFieldDefinition>
        {
            new("/sample_type", "sample_type", "observed_framing", EtktWireKind.Int32),
            new("/history_present", "history_present", "observed_framing", EtktWireKind.Int32),
            new("/history/p_herror_tkt_ptr/$present", "p_herror_tkt_ptr", "observed_framing", EtktWireKind.Int32)
        };
        Add("entry_and_status", "s:h_date_of_entry s:h_time_of_entry s:h_aaa_lniata s:h_aaa_city_code c2:h_aaa_duty_code i:h_aaa_busid c2:h_aaa_sine s:h_aaa_sine_in_nbr c2:h_carrier_code s:h_tkt_nbr c1:h_cpn1_actioned c1:h_cpn2_actioned c1:h_cpn3_actioned c1:h_cpn4_actioned c1:h_status_switch i:h_status i:h_sequence_nbr i:h_indicator i:h_spr_1 c2:h_spr_2 s:glob_tkt_hist_seq_nbr s:h_spr_4");
        Add("issue", "s:h_pax_name s:h_total_fare s:h_fop s:h_exchng_doc s:h_create_date i:h_spr_i1 c2:h_spr_i2 s:h_spr_i3 s:h_spr_i4");
        Add("exchange", "s:h_new_tkt_nbr i:h_new_tkt_cnt s:h_exchng_comment s:h_old_tkt_nbr i:h_spr_e1 c2:h_spr_e2 s:h_spr_e3 s:h_spr_e4");
        Add("refund", "s:h_new_rf_comment s:h_prev_rf_comment i:h_spr_r1 c2:h_spr_r2 s:h_spr_r3 s:h_spr_r4");
        Add("name_adjustment", "s:h_new_name s:h_prev_name i:h_spr_n1 c2:h_spr_n2 s:h_spr_n3 s:h_spr_n4");
        Add("flight_adjustment", "s:h_new_cc s:h_new_flt_nbr s:h_new_date s:h_new_dep s:h_new_arr s:h_new_brd s:h_new_off s:h_new_class c2:h_new_op_cc s:h_new_op_flt_nbr c2:h_new_op_class c2:h_prev_cc s:h_prev_flt_nbr s:h_prev_date s:h_prev_dep s:h_prev_arr s:h_prev_brd s:h_prev_off c2:h_prev_class c2:h_prev_op_cc s:h_prev_op_flt_nbr c2:h_prev_op_class i:h_spr_f1 c2:h_spr_f2 s:h_spr_f3 s:h_spr_f4");
        Add("purge_date_adjustment", "s:h_new_prg_date s:h_new_unflown_date s:h_old_prg_date s:h_old_unflown_date i:h_spr_p1 c2:h_spr_p2 s:h_spr_p3 s:h_spr_p4");
        Add("divide", "s:h_new_recloc s:h_old_recloc");
        Add("fim", "s:h_fim1_cpn1 s:h_fim2_cpn1 s:h_fim1_cpn2 s:h_fim2_cpn2 s:h_fim1_cpn3 s:h_fim2_cpn3 s:h_fim1_cpn4 s:h_fim2_cpn4");
        Add("ato", "s:h_ac_credit_card");
        Add("commission", "s:h_res_comm_amt s:h_comm_pnlty_amt s:h_penalty_amt");
        Add("settlement", "s:h_sac");
        Add("involuntary", "c1:h_inv1 c1:h_inv2 c1:h_inv3 c1:h_inv4 c2:h_cc_inv1 c2:h_cc_inv2 c2:h_cc_inv3 c2:h_cc_inv4 i:h_spr_c1 c2:h_spr_c2 s:h_spr_c3 s:h_spr_c4");
        return new ReadOnlyCollection<EtktFieldDefinition>(fields);

        void Add(string group, string declaration)
        {
            foreach (string token in declaration.Split(' '))
            {
                string[] parts = token.Split(':');
                string kind = parts[0], name = parts[1];
                int count = kind.StartsWith('c') ? int.Parse(kind[1..], System.Globalization.CultureInfo.InvariantCulture) : 1;
                for (int index = 0; index < count; index++)
                {
                    string path = "/history/" + name + (kind.StartsWith('c') ? "/" + index : "");
                    var wireKind = kind == "s" ? EtktWireKind.String : kind == "i" ? EtktWireKind.Int32 : EtktWireKind.Character;
                    fields.Add(new(path, name, group, wireKind));
                }
            }
        }
    }

    #endregion

    #region Properties, Indexers

    /// <summary>
    /// Gets the reviewed ETKT history declarations in wire order.
    /// </summary>
    /// <value>The reviewed ETKT history declarations in wire order.</value>
    public static IReadOnlyList<EtktFieldDefinition> SampleHistory { get; } = Build();

    #endregion
}
