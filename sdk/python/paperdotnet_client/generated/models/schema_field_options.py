from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

@dataclass
class SchemaFieldOptions(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The apply_parents property
    apply_parents: Optional[bool] = False
    # The multiple property
    multiple: Optional[bool] = False
    # The unique property
    unique: Optional[bool] = False
    # The currency_code property
    currency_code: Optional[str] = None
    # The description property
    description: Optional[str] = None
    # The lookup_container_id property
    lookup_container_id: Optional[str] = None
    # The max_length property
    max_length: Optional[int] = None
    # The maximum property
    maximum: Optional[str] = None
    # The minimum property
    minimum: Optional[str] = None
    # The search_weight property
    search_weight: Optional[str] = None
    # The term_set_id property
    term_set_id: Optional[str] = None

    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> SchemaFieldOptions:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: SchemaFieldOptions
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return SchemaFieldOptions()

    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "apply_parents": lambda n : setattr(self, 'apply_parents', n.get_bool_value()),
            "currency_code": lambda n : setattr(self, 'currency_code', n.get_str_value()),
            "description": lambda n : setattr(self, 'description', n.get_str_value()),
            "lookup_container_id": lambda n : setattr(self, 'lookup_container_id', n.get_str_value()),
            "max_length": lambda n : setattr(self, 'max_length', n.get_int_value()),
            "maximum": lambda n : setattr(self, 'maximum', n.get_str_value()),
            "minimum": lambda n : setattr(self, 'minimum', n.get_str_value()),
            "multiple": lambda n : setattr(self, 'multiple', n.get_bool_value()),
            "search_weight": lambda n : setattr(self, 'search_weight', n.get_str_value()),
            "term_set_id": lambda n : setattr(self, 'term_set_id', n.get_str_value()),
            "unique": lambda n : setattr(self, 'unique', n.get_bool_value()),
        }
        return fields

    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_bool_value("apply_parents", self.apply_parents)
        writer.write_str_value("currency_code", self.currency_code)
        writer.write_str_value("description", self.description)
        writer.write_str_value("lookup_container_id", self.lookup_container_id)
        writer.write_int_value("max_length", self.max_length)
        writer.write_str_value("maximum", self.maximum)
        writer.write_str_value("minimum", self.minimum)
        writer.write_bool_value("multiple", self.multiple)
        writer.write_str_value("search_weight", self.search_weight)
        writer.write_str_value("term_set_id", self.term_set_id)
        writer.write_bool_value("unique", self.unique)
        writer.write_additional_data_value(self.additional_data)


