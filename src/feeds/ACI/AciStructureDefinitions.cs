using System.Collections.ObjectModel;

namespace PresidioDemo.Feeds.ACI;

/// <summary>Structure declarations, in wire order. The extension is sample-observed only.</summary>
public static class AciStructureDefinitions
{
    public static IReadOnlyDictionary<string, AciStructureDefinition> All { get; } = Build();

    private static IReadOnlyDictionary<string, AciStructureDefinition> Build()
    {
        var structures = new Dictionary<string, AciStructureDefinition>(StringComparer.Ordinal);
        Add("flight_list", "s:airline_code i:bin_flt_nbr s:flt_date i:nbr_ship_chg i:nbr_legs s:trm_cty i:codeshare_flt_nbr i:last_seq_nbr i:flt_ind i:flt_int1 i:flt_list_int2 i:flt_list_int3 s:flt_list_str1 s:flt_list_str2 s:flt_list_str3 p.leg_list:leg_list p.flight_list:next");
        Add("leg_list", "i:cty_idx_nbr s:cty_code i:leg_ind i:dte_adj i:shp_chg_nbr s:eqp_code s:tail_nbr s:dept_gate s:arrv_gate s:sect_nbr s:pier_nbr i:est_tim_dept s:ste_dte_dept i:std_tim_dept s:std_dte_dept i:brd_time s:brd_dte i:brd_adj i:brd_chg i:nbr_cmpts i:leg_list_int1 i:leg_list_int2 i:leg_list_int3 s:leg_list_str1 s:leg_list_str2 s:leg_list_str3 p.cmpt_list:cmpt_list p.leg_list:next");
        Add("cmpt_list", "i:cmpt_code i:ttl_seats i:ttl_avl i:ttl_less_hld i:ttl_less_hld_sb i:seats_avl i:ttl_kids i:ttl_baby i:ttl_bags i:wt_bal i:cmpt_list_int1 i:cmpt_list_int2 s:cmpt_list_str1 s:cmpt_list_str2 p.cmpt_list:next");
        Add("pax_list", "s:rec_loc i:nbr_prty i:nbr_sur s:pax_name i:pnr_ind i:pax_type s:pass_code i:paxl_id i:pax_list_int1 i:pax_list_int2 i:pax_list_int3 s:pax_list_str1 s:pax_list_str2 s:pax_list_str3 p.paxflt_items:paxflt_items p.pax_list:next");
        Add("paxflt_items", "s:airline_code i:bin_flt_nbr s:flt_date i:pax_id i:brd_off i:seats_un_assgn i:class_code_0 i:class_code_1 i:pax_ind i:pax_ind2 i:spec_ind i:duc_idx i:fnl_dst_idx i:orig_cls_0 i:orig_cls_1 i:cmpt_flown i:chg_cls_0 i:chg_cls_1 i:xfer_to_cls_0 i:xfer_to_cls_1 i:xfer_to_flt s:xfer_to_dte s:xfer_to_alc s:xfer_to_cty i:xfer_frm_flt s:xfer_frm_dte s:xfer_frm_alc s:xfer_frm_cty i:alt_flt i:cons_flt i:stby_ord_nbr i:paxflt_item_int1 i:paxflt_item_int2 i:paxflt_item_int3 s:paxflt_item_str1 s:paxflt_item_str2 s:paxflt_item_str3 p.seat_list:seat_list p.bag_list:bag_list p.prd_list:prd_list p.fqtv_list:fqtv_list p.fcpr_list:fcpr_list p.seq_nbr_list:seq_nbr_list p.sec_prof_list:sec_prof_list p.etkt_list:etkt_list p.tci_list:tci_list p.misc_list:misc_list p.hist_list:hist_list p.cons_flt_list:cons_flt_list p.codeshr_list:codeshr_list p.fqtv_tier_list:fqtv_tier_list p.trans_times_list:trans_times_list p.unresolved_extension:unresolved_extension p.paxflt_items:next");
        Add("seat_list", "i:seat_brd_off i:nbr_seats i:cmpt_id i:seat_typ s:seat_nbr i:seat_int1 s:seat_str1 s:seat_str2 p.seat_list:next");
        Add("bag_list", "i:bag_ind i:bag_cnt i:bag_wt i:carry_cnt i:carry_wt i:nbr_btag_rng i:bagtag_nbr i:nbr_bags s:bag_alc s:bag_dst i:bag_int1 s:bag_str1 s:bag_str2 p.bag_list:next");
        Add("prd_list", "i:prd_flt_lnk i:prd_pax_lnk i:prd_ind i:adult_cnt i:adult_wt i:male_cnt i:male_wt i:female_cnt i:female_wt i:child_cnt i:child_wt i:infant_cnt i:prd_int1 s:prd_str1 s:prd_str2 p.prd_list:next");
        Add("fqtv_list", "i:fqtv_sur_nbr i:fqtv_name_int i:fqtv_ind s:fqtv_nbr s:oa_alc i:fqtv_int1 s:fqtv_str1 s:fqtv_str2 p.fqtv_list:next");
        Add("fcpr_list", "s:fcpr_nbr p.fcpr_list:next i:fcpr_int1 s:fcpr_str1 s:fcpr_str2");
        Add("seq_nbr_list", "i:seq_nbr i:seq_int1 s:seq_str1 s:seq_str2 p.seq_nbr_list:next");
        Add("sec_prof_list", "i:sec_name_int i:sec_cat_0 i:sec_cat_1 i:sec_prof_int1 s:sec_prof_str1 s:sec_prof_str2 p.sec_prof_list:next");
        Add("etkt_list", "i:etkt_name_int i:etkt_int1 s:etkt_str1 s:etkt_str2 p.etkt_list:next");
        Add("tci_list", "i:tci_ind s:oa_flt_urp s:oa_seat_nbr s:oa_bag_dst s:ob_alc s:ob_dept s:ob_arrv i:ob_flt_nbr s:loc_dte i:loc_time s:pax_seq_nbr i:tci_int1 s:tci_str1 s:tci_str2 p.tci_list:next");
        Add("misc_list", "i:misc_ind i:misc_int1 s:misc_str1 s:misc_str2 p.misc_list:next");
        Add("hist_list", "s:aaa_cty i:agt_int_0 i:agt_int_1 i:agt_dc_0 i:agt_dc_1 i:ln_ia_ta i:hist_int1 s:hist_str1 s:hist_str2 p.hist_list:next");
        Add("cons_flt_list", "s:brd_pnt s:off_pnt s:brd_date i:diff_indx i:cons_flt_int1 s:cons_flt_str1 s:cons_flt_str2 p.cons_flt_list:next");
        Add("codeshr_list", "i:oa_carcode_0 i:oa_carcode_1 i:oa_flt_nbr i:oa_cls_0 i:oa_cls_1 s:oa_pnr i:codeshr_ind i:codeshr_int1 s:codeshr_str1 s:codeshr_str2 p.codeshr_list:next");
        Add("fqtv_tier_list", "i:pax_initial i:tier_level i:cust_val i:fqtv_tier_int1 s:fqtv_tier_str1 s:fqtv_tier_str2 p.fqtv_tier_list:next");
        Add("trans_times_list", "s:ckin_time s:ckin_date s:transfer_time s:transfer_date s:trans_time1 s:trans_date1 s:trans_time2 s:trans_date2 s:trans_time3 s:trans_date3 p.trans_times_list:next");
        Add("unresolved_extension", "i:word1 i:word2 i:word3 s:unresolvedIdentifier s:unresolvedRecordReference p.unresolved_extension:next", true);
        return new ReadOnlyDictionary<string, AciStructureDefinition>(structures);

        void Add(string name, string declaration, bool observed = false)
        {
            var fields = declaration.Split(' ').Select(token =>
            {
                var parts = token.Split(':');
                var kind = parts[0] == "s" ? AciWireKind.String :
                    parts[0] == "i" ? AciWireKind.Int32 : AciWireKind.Pointer;
                return new AciFieldDefinition(parts[1], kind, kind == AciWireKind.Pointer ? parts[0][2..] : "");
            }).ToArray();
            structures.Add(name, new(name, Array.AsReadOnly(fields), observed));
        }
    }
}
